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
    }

    public struct Cell
    {
        public Vector3 Position;
        public Vector3 Normal;
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
            public string Name;
        }

        private static readonly Dictionary<GameObject, PrefabInfo> s_prefabInfo = new Dictionary<GameObject, PrefabInfo>();

        private readonly Dictionary<Heightmap, List<Entry>> _entriesByHeightmap = new Dictionary<Heightmap, List<Entry>>();
        private readonly List<Entry> _baseEntries = new List<Entry>();
        private readonly Dictionary<string, CreatureInfo> _creatures = new Dictionary<string, CreatureInfo>();

        private Cell[] _cells = new Cell[0];
        private Cell[] _front = new Cell[0];
        private Dictionary<string, CreatureInfo> _frontCreatures = new Dictionary<string, CreatureInfo>();

        private int _size;          // cells per side
        private float _cellSize;
        private Vector3 _origin;    // world position of cell (0,0) centre
        private int _next = -1;     // next cell to process, -1 = idle

        private bool _isDay, _isNight;

        public Cell[] Cells => _front;
        public int Version { get; private set; }
        public Vector3 Center { get; private set; }
        public float Radius { get; private set; }
        public IReadOnlyDictionary<string, CreatureInfo> Creatures => _frontCreatures;
        public bool Busy => _next >= 0;

        public void Begin(Vector3 center, float radius, float cellSize)
        {
            _cellSize = Mathf.Max(0.5f, cellSize);
            _size = Mathf.Max(2, Mathf.CeilToInt(radius * 2f / _cellSize));
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

        private Cell Evaluate(ZoneSystem zs, Vector3 p)
        {
            var cell = new Cell();
            zs.GetGroundData(ref p, out Vector3 normal, out Heightmap.Biome biome, out Heightmap.BiomeArea biomeArea, out Heightmap hmap);
            cell.Position = p;
            cell.Normal = normal;
            if (hmap == null) return cell;            // terrain not loaded
            if (zs.IsBlocked(p)) return cell;         // under a building / tree / rock

            float altitude = p.y - 30f;               // 30 = sea level
            float tiltY = normal.y;
            bool inBase = EffectArea.IsPointInsideArea(p, EffectArea.Type.PlayerBase) != null;

            // Lazily computed, these are comparatively expensive
            int inForest = -1, inLava = -1;
            float oceanDepth = float.NaN;
            float centerDist = Utils.LengthXZ(p);

            CellState best = CellState.None;
            List<Entry> entries = GetEntries(hmap);
            for (int e = 0; e < entries.Count; e++)
            {
                Entry entry = entries[e];
                SpawnSystem.SpawnData s = entry.Data;

                if ((s.m_biome & biome) == 0) continue;
                if ((s.m_biomeArea & biomeArea) == 0) continue;
                if (altitude < s.m_minAltitude || altitude > s.m_maxAltitude) continue;
                if (tiltY < Mathf.Cos(Mathf.Deg2Rad * s.m_maxTilt) || tiltY > Mathf.Cos(Mathf.Deg2Rad * s.m_minTilt)) continue;
                if (s.m_minDistanceFromCenter > 0f && centerDist < s.m_minDistanceFromCenter) continue;
                if (s.m_maxDistanceFromCenter > 0f && centerDist > s.m_maxDistanceFromCenter) continue;

                if (!s.m_inForest || !s.m_outsideForest)
                {
                    if (inForest < 0) inForest = WorldGenerator.InForest(p) ? 1 : 0;
                    if (!s.m_inForest && inForest == 1) continue;
                    if (!s.m_outsideForest && inForest == 0) continue;
                }
                if (!s.m_inLava || !s.m_outsideLava)
                {
                    if (inLava < 0) inLava = hmap.IsLava(p) ? 1 : 0;
                    if (!s.m_inLava && inLava == 1) continue;
                    if (!s.m_outsideLava && inLava == 0) continue;
                }
                if (s.m_minOceanDepth != s.m_maxOceanDepth)
                {
                    if (float.IsNaN(oceanDepth)) oceanDepth = hmap.GetOceanDepth(p);
                    if (oceanDepth < s.m_minOceanDepth || oceanDepth > s.m_maxOceanDepth) continue;
                }

                CellState state;
                if (!s.m_insidePlayerBase && inBase)
                {
                    if (!entry.Hostile) continue;
                    state = CellState.Protected;
                }
                else if (!entry.Hostile)
                {
                    state = CellState.Passive;
                }
                else
                {
                    state = IsActiveNow(entry) ? CellState.HostileNow : CellState.HostileLater;
                }

                if (state != CellState.Protected) RecordCreature(entry, state == CellState.HostileNow || !entry.Hostile && IsActiveNow(entry));
                if (state > best) best = state;
                if (best == CellState.HostileNow) break;
            }

            cell.State = best;
            return cell;
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

            info = new PrefabInfo { Hostile = hostile, Name = name };
            s_prefabInfo[prefab] = info;
            return info;
        }

        public static string Localize(string token)
        {
            return Localization.instance != null ? Localization.instance.Localize(token) : token;
        }
    }
}
