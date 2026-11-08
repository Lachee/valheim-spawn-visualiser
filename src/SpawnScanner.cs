using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace SpawnVisualiser
{
    /// <summary>What can happen at a single grid cell, ordered by priority (higher wins).</summary>
    public enum CellState : byte
    {
        None = 0,          // Nothing can spawn (blocked, wrong terrain, unloaded...)
        Passive = 1,       // Only non-hostile creatures (deer, fish, birds...)
        Protected = 2,     // Hostiles could spawn here, but a player base (workbench, fire...) prevents it
        HostileLater = 3,  // Hostiles can spawn here, but not under current conditions (time of day / weather)
        HostileNow = 4,    // Hostiles can spawn here right now
        Blocked = 5,       // Not evaluated for priority: something over the ground (or non-terrain ground) blocks all spawns
    }

    public struct Cell
    {
        public Vector3 Position;
        public Vector3 Normal;
        public Heightmap Heightmap;
        public CellState State;
    }

    public class CreatureInfo
    {
        public string Name;
        public bool Hostile;
        public bool Now;
        public bool Event;
    }

    /// <summary>
    /// Samples a grid around a centre point, replicating the checks in SpawnSystem.IsSpawnPointGood
    /// (minus the "too close to a player" check, which is shown as a ring instead).
    /// Work is time-sliced across frames so it never causes a hitch.
    /// </summary>
    public class SpawnScanner
    {
        private class Entry
        {
            public SpawnSystem.SpawnData Data;
            public bool Hostile;
            public bool IsEvent;
            public string Name;
        }

        private static readonly AccessTools.FieldRef<List<SpawnSystem>> s_spawnSystems =
            AccessTools.StaticFieldRefAccess<List<SpawnSystem>>(AccessTools.Field(typeof(SpawnSystem), "m_instances"));

        private class PrefabInfo
        {
            public bool Hostile;
            public bool IsCreature;
            public string Name;
        }

        private static readonly Dictionary<GameObject, PrefabInfo> s_prefabInfo = new Dictionary<GameObject, PrefabInfo>();

        private readonly Dictionary<Heightmap, List<Entry>> _entriesByHeightmap = new Dictionary<Heightmap, List<Entry>>();
        private readonly List<Entry> _baseEntries = new List<Entry>();
        private readonly Dictionary<string, CreatureInfo> _creatures = new Dictionary<string, CreatureInfo>();

        private Cell[] _cells = new Cell[0];
        private Cell[] _front = new Cell[0];
        private Dictionary<string, CreatureInfo> _frontCreatures = new Dictionary<string, CreatureInfo>();

        private int _size;          // cells per side (always even)
        private int _cellSize;      // metres; integer so tile edges line up with the 1 m terrain grid
        private Vector3 _origin;    // world position of cell (0,0) centre
        private int _next = -1;     // next cell to process, -1 = idle

        private bool _isDay, _isNight;

        public Cell[] Cells => _front;
        public int Size => _size;
        public int CellSize => _cellSize;
        /// <summary>World X/Z of the minimum corner of the grid (always whole metres).</summary>
        public Vector3 GridMin => new Vector3(_origin.x - _cellSize * 0.5f, 0f, _origin.z - _cellSize * 0.5f);
        public int Version { get; private set; }
        public Vector3 Center { get; private set; }
        public float Radius { get; private set; }
        public IReadOnlyDictionary<string, CreatureInfo> Creatures => _frontCreatures;
        public bool Busy => _next >= 0;

        public void Begin(Vector3 center, float radius, int cellSize)
        {
            _cellSize = Mathf.Max(1, cellSize);
            _size = Mathf.Max(2, Mathf.CeilToInt(radius * 2f / _cellSize));
            if ((_size & 1) == 1) _size++;  // even count keeps the grid edges on whole metres
            // Snap to the cell grid so tiles don't "swim" as the player walks.
            float half = _size * _cellSize * 0.5f;
            float sx = Mathf.Round(center.x / _cellSize) * _cellSize;
            float sz = Mathf.Round(center.z / _cellSize) * _cellSize;
            _origin = new Vector3(sx - half + _cellSize * 0.5f, 0f, sz - half + _cellSize * 0.5f);
            Center = new Vector3(sx, center.y, sz);
            Radius = radius;

            int count = _size * _size;
            if (_cells.Length != count) _cells = new Cell[count];

            _isDay = EnvMan.IsDay();
            _isNight = EnvMan.IsNight();
            _creatures.Clear();
            _entriesByHeightmap.Clear();
            BuildBaseEntries();
            _next = 0;
        }

        /// <summary>Process up to <paramref name="budget"/> cells. Returns true when a full scan has completed.</summary>
        public bool Step(int budget)
        {
            if (_next < 0) return false;
            ZoneSystem zs = ZoneSystem.instance;
            if (zs == null) { _next = -1; return false; }

            float r2 = Radius * Radius;
            int count = _size * _size;
            int end = Mathf.Min(count, _next + budget);
            for (int i = _next; i < end; i++)
            {
                int x = i % _size, z = i / _size;
                Vector3 p = new Vector3(_origin.x + x * _cellSize, 0f, _origin.z + z * _cellSize);
                float dx = p.x - Center.x, dz = p.z - Center.z;
                if (dx * dx + dz * dz > r2)
                {
                    _cells[i] = default;
                    continue;
                }
                _cells[i] = Evaluate(zs, p);
            }
            _next = end;

            if (_next >= count)
            {
                _next = -1;
                // Swap buffers so the renderer always sees a complete scan.
                Cell[] tmp = _front;
                _front = _cells;
                _cells = tmp;
                _frontCreatures = new Dictionary<string, CreatureInfo>(_creatures);
                Version++;
                return true;
            }
            return false;
        }

        private static readonly int s_blockMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece");
        private static readonly int s_terrainMask = LayerMask.GetMask("terrain");

        /// <summary>Everything about a ground point the spawn rules look at. Expensive bits are computed lazily.</summary>
        private struct PointData
        {
            public Vector3 P;
            public Heightmap Hmap;
            public Heightmap.Biome Biome;
            public Heightmap.BiomeArea Area;
            public float Altitude, TiltY, CenterDist, OceanDepth;
            public int InForest, InLava;
            public bool InBase;

            public static PointData Create(Vector3 p, Vector3 normal, Heightmap.Biome biome, Heightmap.BiomeArea area, Heightmap hmap)
            {
                return new PointData
                {
                    P = p, Hmap = hmap, Biome = biome, Area = area,
                    Altitude = p.y - 30f, // 30 = sea level
                    TiltY = normal.y,
                    CenterDist = Utils.LengthXZ(p),
                    OceanDepth = float.NaN, InForest = -1, InLava = -1,
                    InBase = EffectArea.IsPointInsideArea(p, EffectArea.Type.PlayerBase) != null,
                };
            }
        }

        /// <summary>Mirrors SpawnSystem.IsSpawnPointGood for one spawner. Returns null if OK, else why not.</summary>
        private static string Check(SpawnSystem.SpawnData s, ref PointData d)
        {
            if ((s.m_biome & d.Biome) == 0) return "wrong biome";
            if ((s.m_biomeArea & d.Area) == 0) return "wrong part of biome (edge/median)";
            if (d.Altitude < s.m_minAltitude) return "too low";
            if (d.Altitude > s.m_maxAltitude) return "too high";
            if (d.TiltY < Mathf.Cos(Mathf.Deg2Rad * s.m_maxTilt)) return "too steep";
            if (d.TiltY > Mathf.Cos(Mathf.Deg2Rad * s.m_minTilt)) return "too flat";
            if (s.m_minDistanceFromCenter > 0f && d.CenterDist < s.m_minDistanceFromCenter) return "too close to world centre";
            if (s.m_maxDistanceFromCenter > 0f && d.CenterDist > s.m_maxDistanceFromCenter) return "too far from world centre";
            if (!s.m_inForest || !s.m_outsideForest)
            {
                if (d.InForest < 0) d.InForest = WorldGenerator.InForest(d.P) ? 1 : 0;
                if (!s.m_inForest && d.InForest == 1) return "in forest";
                if (!s.m_outsideForest && d.InForest == 0) return "not in forest";
            }
            if (!s.m_inLava || !s.m_outsideLava)
            {
                if (d.InLava < 0) d.InLava = d.Hmap.IsLava(d.P) ? 1 : 0;
                if (!s.m_inLava && d.InLava == 1) return "on lava";
                if (!s.m_outsideLava && d.InLava == 0) return "not on lava";
            }
            if (s.m_minOceanDepth != s.m_maxOceanDepth)
            {
                if (float.IsNaN(d.OceanDepth)) d.OceanDepth = d.Hmap.GetOceanDepth(d.P);
                if (d.OceanDepth < s.m_minOceanDepth) return "water too shallow";
                if (d.OceanDepth > s.m_maxOceanDepth) return "water too deep";
            }
            return null;
        }

        private CellState StateFor(Entry entry, ref PointData d)
        {
            if (!entry.Data.m_insidePlayerBase && d.InBase)
                return entry.Hostile ? CellState.Protected : CellState.None;
            if (!entry.Hostile) return CellState.Passive;
            return IsActiveNow(entry) ? CellState.HostileNow : CellState.HostileLater;
        }

        private Cell Evaluate(ZoneSystem zs, Vector3 p)
        {
            var cell = new Cell();
            zs.GetGroundData(ref p, out Vector3 normal, out Heightmap.Biome biome, out Heightmap.BiomeArea biomeArea, out Heightmap hmap);
            cell.Position = p;
            cell.Normal = normal;
            cell.Heightmap = hmap;
            if (hmap == null)
            {
                // Hit something on the terrain layer that isn't a heightmap (or nothing at all): the game can't spawn here either.
                if (Physics.Raycast(p + Vector3.up * 5000f, Vector3.down, 10000f, s_terrainMask)) cell.State = CellState.Blocked;
                return cell;
            }
            if (zs.IsBlocked(p)) { cell.State = CellState.Blocked; return cell; }

            PointData d = PointData.Create(p, normal, biome, biomeArea, hmap);
            CellState best = CellState.None;
            List<Entry> entries = GetEntries(hmap);
            for (int e = 0; e < entries.Count; e++)
            {
                Entry entry = entries[e];
                if (Check(entry.Data, ref d) != null) continue;
                CellState state = StateFor(entry, ref d);
                if (state == CellState.None) continue;

                if (state != CellState.Protected) RecordCreature(entry, state == CellState.HostileNow || !entry.Hostile && IsActiveNow(entry));
                if (state > best) best = state;
                if (best == CellState.HostileNow) break;
            }

            cell.State = best;
            return cell;
        }

        /// <summary>Human-readable breakdown of why things can or can't spawn at a point (for the crosshair inspector).</summary>
        public List<string> Explain(Vector3 point)
        {
            var lines = new List<string>();
            ZoneSystem zs = ZoneSystem.instance;
            if (zs == null) return lines;
            if (_baseEntries.Count == 0) BuildBaseEntries();

            Vector3 p = point;
            zs.GetGroundData(ref p, out Vector3 normal, out Heightmap.Biome biome, out Heightmap.BiomeArea area, out Heightmap hmap);
            if (hmap == null)
            {
                if (Physics.Raycast(point + Vector3.up * 5000f, Vector3.down, out RaycastHit th, 10000f, s_terrainMask))
                    lines.Add($"<color=#bbbbbb>Ground is '{Describe(th.collider)}', not terrain: nothing spawns</color>");
                else
                    lines.Add("<color=#bbbbbb>No terrain loaded here</color>");
                return lines;
            }

            if (Physics.Raycast(p + Vector3.up * 2000f, Vector3.down, out RaycastHit bh, 10000f, s_blockMask))
            {
                float dy = bh.point.y - p.y;
                string where = dy > 0.5f ? $"{dy:0} m above" : dy < -0.5f ? $"{-dy:0} m below" : "on the ground";
                lines.Add($"<color=#bbbbbb>Blocked by '{Describe(bh.collider)}' ({LayerMask.LayerToName(bh.collider.gameObject.layer)}{(bh.collider.isTrigger ? ", trigger" : "")}, {where})</color>");
                return lines;
            }

            PointData d = PointData.Create(p, normal, biome, area, hmap);
            float slope = Mathf.Acos(Mathf.Clamp(normal.y, -1f, 1f)) * Mathf.Rad2Deg;
            lines.Add($"{biome} · {d.Altitude:0} m above sea · {slope:0}° slope{(d.InBase ? " · <color=#66ee88>in base</color>" : "")}");

            // Collapse spawner entries by creature: best result wins.
            var results = new Dictionary<string, string>();
            var order = new List<string>();
            foreach (Entry entry in GetEntries(hmap))
            {
                string fail = Check(entry.Data, ref d);
                if (fail == "wrong biome") continue; // noise
                string text;
                if (fail == null)
                {
                    CellState st = StateFor(entry, ref d);
                    text = st == CellState.HostileNow ? "<color=#ff6655>can spawn now</color>"
                         : st == CellState.HostileLater ? "<color=#ffaa33>can spawn later</color>"
                         : st == CellState.Passive ? "<color=#66bbff>can spawn</color>"
                         : "<color=#66ee88>blocked by base</color>";
                }
                else text = $"<color=#999999>{fail}</color>";

                if (!results.TryGetValue(entry.Name, out string prev)) { order.Add(entry.Name); results[entry.Name] = text; }
                else if (fail == null && prev.Contains("#999999")) results[entry.Name] = text;
            }
            order.Sort((a, b) => results[b].Contains("#999999").CompareTo(results[a].Contains("#999999")));
            int shown = 0;
            foreach (string name in order)
            {
                if (shown++ >= 6) { lines.Add($"<color=#999999>…and {order.Count - 6} more</color>"); break; }
                lines.Add($"{name}: {results[name]}");
            }
            if (order.Count == 0) lines.Add("<color=#999999>No spawners for this biome</color>");
            return lines;
        }

        private static string Describe(Collider c)
        {
            Transform root = c.transform.root;
            string name = root != null ? root.name : c.name;
            return name.Replace("(Clone)", "").Trim();
        }

        private bool IsActiveNow(Entry e)
        {
            SpawnSystem.SpawnData s = e.Data;
            if (!s.m_spawnAtDay && _isDay) return false;
            if (!s.m_spawnAtNight && _isNight) return false;
            if (s.m_requiredEnvironments.Count > 0 && (EnvMan.instance == null || !EnvMan.instance.IsEnvironment(s.m_requiredEnvironments))) return false;
            if (!string.IsNullOrEmpty(s.m_requiredPersistentEvent)) return false;
            return true;
        }

        private void RecordCreature(Entry e, bool now)
        {
            if (!_creatures.TryGetValue(e.Name, out CreatureInfo info))
            {
                info = new CreatureInfo { Name = e.Name, Hostile = e.Hostile, Event = e.IsEvent };
                _creatures[e.Name] = info;
            }
            info.Now |= now;
        }

        private void BuildBaseEntries()
        {
            _baseEntries.Clear();
            List<SpawnSystem> systems = s_spawnSystems();
            if (systems == null || systems.Count == 0) return;

            // Every zone's SpawnSystem shares the same lists (from the _ZoneCtrl prefab), so one is enough.
            SpawnSystem sys = systems[0];
            foreach (SpawnSystemList list in sys.m_spawnLists)
                foreach (SpawnSystem.SpawnData s in list.m_spawners)
                    TryAdd(_baseEntries, s, false);

            // Active raid / random event
            List<SpawnSystem.SpawnData> eventSpawners = RandEventSystem.instance?.GetCurrentSpawners();
            if (eventSpawners != null)
                foreach (SpawnSystem.SpawnData s in eventSpawners)
                    TryAdd(_baseEntries, s, true);
        }

        private List<Entry> GetEntries(Heightmap hmap)
        {
            if (_entriesByHeightmap.TryGetValue(hmap, out List<Entry> list)) return list;

            List<AltBiome> alts = hmap.m_cornerAltBiomes;
            if (alts == null || alts.Count == 0)
            {
                list = _baseEntries;
            }
            else
            {
                list = new List<Entry>(_baseEntries.Count + 8);
                foreach (Entry e in _baseEntries)
                {
                    bool blocked = false;
                    if (!e.IsEvent)
                        foreach (AltBiome alt in alts)
                            if (alt.m_blockSpawnNames.Contains(e.Data.m_name)) { blocked = true; break; }
                    if (!blocked) list.Add(e);
                }
                foreach (AltBiome alt in alts)
                    foreach (SpawnSystem.SpawnData s in alt.m_spawn)
                        TryAdd(list, s, false);
            }
            _entriesByHeightmap[hmap] = list;
            return list;
        }

        private static void TryAdd(List<Entry> list, SpawnSystem.SpawnData s, bool isEvent)
        {
            if (s == null || !s.m_enabled || s.m_prefab == null || s.m_spawnChance <= 0f) return;
            // Progression-locked spawns (e.g. "defeated_bonemass") are treated as impossible until unlocked.
            if (!string.IsNullOrEmpty(s.m_requiredGlobalKey) &&
                (ZoneSystem.instance == null || !ZoneSystem.instance.GetGlobalKey(s.m_requiredGlobalKey))) return;

            PrefabInfo info = GetPrefabInfo(s.m_prefab);
            if (!info.IsCreature) return; // e.g. weather meteors / projectiles
            list.Add(new Entry { Data = s, Hostile = info.Hostile, IsEvent = isEvent, Name = info.Name });
        }

        private static PrefabInfo GetPrefabInfo(GameObject prefab)
        {
            if (s_prefabInfo.TryGetValue(prefab, out PrefabInfo info)) return info;

            bool hostile = false;
            string name = prefab.name;
            Character c = prefab.GetComponent<Character>();
            if (c != null)
            {
                switch (c.m_faction)
                {
                    case Character.Faction.Players:
                    case Character.Faction.AnimalsVeg:
                    case Character.Faction.PlayerSpawned:
                    case Character.Faction.TrainingDummy:
                    case Character.Faction.Dverger:
                        hostile = false;
                        break;
                    default:
                        hostile = true;
                        break;
                }
                if (!string.IsNullOrEmpty(c.m_name)) name = Localize(c.m_name);
            }
            else
            {
                ItemDrop item = prefab.GetComponent<ItemDrop>();
                if (item != null && !string.IsNullOrEmpty(item.m_itemData?.m_shared?.m_name))
                    name = Localize(item.m_itemData.m_shared.m_name);
            }

            bool isCreature = c != null || prefab.GetComponent<Fish>() != null;
            info = new PrefabInfo { Hostile = hostile, IsCreature = isCreature, Name = name };
            s_prefabInfo[prefab] = info;
            return info;
        }

        public static string Localize(string token)
        {
            return Localization.instance != null ? Localization.instance.Localize(token) : token;
        }
    }
}
