using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace SpawnVisualiser
{
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "lachee.spawnvisualiser";
        public const string Name = "Spawn Visualiser";
        public const string Version = "1.0.0";

        internal static ManualLogSource Log;

        // General
        internal static ConfigEntry<KeyboardShortcut> ToggleKey;
        internal static ConfigEntry<float> ScanRadius;
        internal static ConfigEntry<float> CellSize;
        internal static ConfigEntry<float> RefreshInterval;
        internal static ConfigEntry<int> CellsPerFrame;
        // Display
        internal static ConfigEntry<float> Opacity;
        internal static ConfigEntry<bool> ShowPassive;
        internal static ConfigEntry<bool> ShowProtected;
        internal static ConfigEntry<bool> ShowHostileLater;
        internal static ConfigEntry<bool> ShowRings;
        internal static ConfigEntry<bool> ShowSpawners;
        internal static ConfigEntry<bool> ShowLegend;
        internal static ConfigEntry<Vector2> LegendPosition;
        // Colours
        internal static ConfigEntry<Color> ColorHostileNow;
        internal static ConfigEntry<Color> ColorHostileLater;
        internal static ConfigEntry<Color> ColorPassive;
        internal static ConfigEntry<Color> ColorProtected;

        private static readonly AccessTools.FieldRef<List<CreatureSpawner>> s_creatureSpawners =
            AccessTools.StaticFieldRefAccess<List<CreatureSpawner>>(AccessTools.Field(typeof(CreatureSpawner), "m_creatureSpawners"));

        private SpawnScanner _scanner;
        private OverlayRenderer _overlay;
        private bool _enabled;
        private float _nextScan;
        private bool _dirty;

        private readonly List<SpawnerMarker> _markers = new List<SpawnerMarker>();
        private float _nextMarkerRefresh;

        private struct SpawnerMarker
        {
            public Transform Transform;
            public string Title;
            public string Subtitle;
            public bool Hostile;
        }

        private void Awake()
        {
            Log = Logger;
            _scanner = new SpawnScanner();

            ToggleKey = Config.Bind("General", "Toggle key", new KeyboardShortcut(KeyCode.J),
                "Key that toggles the spawn overlay on and off.");
            ScanRadius = Config.Bind("General", "Scan radius", 64f,
                new ConfigDescription("How far around you (in metres) spawnable ground is shown. World spawns happen 40-80 m from players.",
                    new AcceptableValueRange<float>(16f, 128f)));
            CellSize = Config.Bind("General", "Tile size", 2f,
                new ConfigDescription("Size of each overlay tile in metres. Smaller = more detail but slower scans.",
                    new AcceptableValueRange<float>(1f, 8f)));
            RefreshInterval = Config.Bind("General", "Refresh interval", 1.5f,
                new ConfigDescription("Seconds between overlay refreshes.", new AcceptableValueRange<float>(0.25f, 10f)));
            CellsPerFrame = Config.Bind("General", "Tiles per frame", 600,
                new ConfigDescription("How many tiles are evaluated per frame. Lower this if you notice stutter.",
                    new AcceptableValueRange<int>(50, 5000)));

            Opacity = Config.Bind("Display", "Opacity", 0.8f,
                new ConfigDescription("Overall opacity of the overlay tiles.", new AcceptableValueRange<float>(0.1f, 1f)));
            ShowPassive = Config.Bind("Display", "Show passive spawns", true, "Show areas where only passive animals (deer, fish, birds...) can spawn.");
            ShowProtected = Config.Bind("Display", "Show protected areas", true, "Show areas where your base (workbench, campfire, etc.) is blocking hostile spawns.");
            ShowHostileLater = Config.Bind("Display", "Show conditional hostile spawns", true, "Show areas where hostiles can spawn, but not right now (e.g. only at night or in certain weather).");
            ShowRings = Config.Bind("Display", "Show range rings", true, "Show the 40 m ring (mobs never spawn closer to you) and the 80 m ring (outer edge of world spawns).");
            ShowSpawners = Config.Bind("Display", "Show spawner labels", true, "Label nests, spawners and fixed creature spawn points nearby.");
            ShowLegend = Config.Bind("Display", "Show legend", true, "Show the legend panel while the overlay is on.");
            LegendPosition = Config.Bind("Display", "Legend position", new Vector2(0.01f, 0.32f),
                "Legend top-left corner as a fraction of the screen (0,0 = top-left, 1,1 = bottom-right).");

            ColorHostileNow = Config.Bind("Colours", "Hostile now", new Color(0.95f, 0.18f, 0.15f, 0.55f), "Hostile creatures can spawn here right now.");
            ColorHostileLater = Config.Bind("Colours", "Hostile later", new Color(1f, 0.62f, 0.1f, 0.40f), "Hostile creatures can spawn here under other conditions (night, weather).");
            ColorPassive = Config.Bind("Colours", "Passive only", new Color(0.3f, 0.7f, 1f, 0.30f), "Only passive creatures can spawn here.");
            ColorProtected = Config.Bind("Colours", "Protected", new Color(0.25f, 0.95f, 0.45f, 0.35f), "A player base is preventing hostile spawns here.");

            Config.SettingChanged += (_, __) => _dirty = true;

            Log.LogInfo($"{Name} {Version} loaded. Press {ToggleKey.Value} in-game to toggle.");
        }

        private void OnDestroy()
        {
            _overlay?.Destroy();
        }

        internal static bool IsStateShown(CellState s)
        {
            switch (s)
            {
                case CellState.Passive: return ShowPassive.Value;
                case CellState.Protected: return ShowProtected.Value;
                case CellState.HostileLater: return ShowHostileLater.Value;
                case CellState.HostileNow: return true;
                default: return false;
            }
        }

        internal static Color ColorFor(CellState s)
        {
            switch (s)
            {
                case CellState.Passive: return ColorPassive.Value;
                case CellState.Protected: return ColorProtected.Value;
                case CellState.HostileLater: return ColorHostileLater.Value;
                case CellState.HostileNow: return ColorHostileNow.Value;
                default: return Color.clear;
            }
        }

        private static bool IsTyping()
        {
            return (Chat.instance != null && Chat.instance.HasFocus())
                   || Console.IsVisible()
                   || TextInput.IsVisible()
                   || Minimap.InTextInput()
                   || Menu.IsVisible();
        }

        private void Update()
        {
            Player player = Player.m_localPlayer;

            if (player != null && ToggleKey.Value.IsDown() && !IsTyping())
            {
                _enabled = !_enabled;
                _nextScan = 0f;
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft,
                    _enabled ? "Spawn visualiser: ON" : "Spawn visualiser: OFF");
            }

            bool active = _enabled && player != null && ZoneSystem.instance != null;
            if (!active)
            {
                _overlay?.SetVisible(false);
                return;
            }

            if (_overlay == null) _overlay = new OverlayRenderer();
            _overlay.SetVisible(true);
            _overlay.SetRingsVisible(ShowRings.Value);
            _overlay.UpdateRings(player.transform.position);

            if (_dirty && !_scanner.Busy)
            {
                _dirty = false;
                _nextScan = 0f;
            }

            if (!_scanner.Busy && Time.time >= _nextScan)
                _scanner.Begin(player.transform.position, ScanRadius.Value, CellSize.Value);

            if (_scanner.Step(CellsPerFrame.Value))
            {
                _overlay.Rebuild(_scanner, CellSize.Value);
                _nextScan = Time.time + RefreshInterval.Value;
            }

            if (ShowSpawners.Value && Time.time >= _nextMarkerRefresh)
            {
                _nextMarkerRefresh = Time.time + 2f;
                RefreshMarkers(player.transform.position);
            }
        }

        // ---------------------------------------------------------------- Spawner markers

        private void RefreshMarkers(Vector3 center)
        {
            _markers.Clear();
            float range = ScanRadius.Value + 16f;
            float range2 = range * range;

            foreach (SpawnArea area in Object.FindObjectsByType<SpawnArea>(FindObjectsSortMode.None))
            {
                if (area == null || (area.transform.position - center).sqrMagnitude > range2) continue;
                var names = area.m_prefabs.Where(p => p?.m_prefab != null)
                    .Select(p => CreatureName(p.m_prefab)).Distinct().ToList();
                Hoverable hover = area.GetComponentInParent<Hoverable>();
                string title = hover != null ? SpawnScanner.Localize(hover.GetHoverName()) : "Spawner";
                if (string.IsNullOrEmpty(title)) title = "Spawner";
                _markers.Add(new SpawnerMarker
                {
                    Transform = area.transform,
                    Title = title,
                    Subtitle = "Spawns " + string.Join(", ", names) + $" every {area.m_spawnIntervalSec:0}s (max {area.m_maxNear} nearby)",
                    Hostile = area.m_prefabs.Any(p => p?.m_prefab != null && IsHostile(p.m_prefab)),
                });
            }

            List<CreatureSpawner> spawners = s_creatureSpawners();
            if (spawners != null)
            {
                foreach (CreatureSpawner cs in spawners)
                {
                    if (cs == null || cs.m_creaturePrefab == null) continue;
                    if ((cs.transform.position - center).sqrMagnitude > range2) continue;
                    string when = cs.m_spawnAtDay && cs.m_spawnAtNight ? "" : cs.m_spawnAtNight ? ", night only" : ", day only";
                    string respawn = cs.m_respawnTimeMinuts > 0f ? $"respawns {cs.m_respawnTimeMinuts:0} min" : "no respawn";
                    _markers.Add(new SpawnerMarker
                    {
                        Transform = cs.transform,
                        Title = CreatureName(cs.m_creaturePrefab),
                        Subtitle = $"Spawn point ({respawn}{when})",
                        Hostile = IsHostile(cs.m_creaturePrefab),
                    });
                }
            }
        }

        private static string CreatureName(GameObject prefab)
        {
            Character c = prefab.GetComponent<Character>();
            return c != null && !string.IsNullOrEmpty(c.m_name) ? SpawnScanner.Localize(c.m_name) : prefab.name;
        }

        private static bool IsHostile(GameObject prefab)
        {
            Character c = prefab.GetComponent<Character>();
            if (c == null) return false;
            return c.m_faction != Character.Faction.AnimalsVeg && c.m_faction != Character.Faction.Players &&
                   c.m_faction != Character.Faction.PlayerSpawned && c.m_faction != Character.Faction.Dverger &&
                   c.m_faction != Character.Faction.TrainingDummy;
        }

        // ---------------------------------------------------------------- GUI

        private GUIStyle _panelStyle, _titleStyle, _textStyle, _smallStyle, _labelTitle, _labelSub;
        private Texture2D _panelTex, _whiteTex;

        private void InitStyles()
        {
            if (_panelStyle != null) return;
            _panelTex = MakeTex(new Color(0.06f, 0.05f, 0.04f, 0.78f));
            _whiteTex = MakeTex(Color.white);

            _panelStyle = new GUIStyle { normal = { background = _panelTex }, padding = new RectOffset(12, 12, 10, 10) };
            _titleStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, normal = { textColor = new Color(1f, 0.85f, 0.55f) } };
            _textStyle = new GUIStyle(GUI.skin.label) { normal = { textColor = new Color(0.92f, 0.9f, 0.86f) }, wordWrap = false };
            _smallStyle = new GUIStyle(_textStyle) { normal = { textColor = new Color(0.75f, 0.73f, 0.7f) } };
            _labelTitle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.LowerCenter, fontStyle = FontStyle.Bold };
            _labelSub = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperCenter };
        }

        private static Texture2D MakeTex(Color c)
        {
            var t = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }

        private void OnGUI()
        {
            if (!_enabled || Player.m_localPlayer == null) return;
            if (Hud.instance != null && Hud.IsUserHidden()) return;
            InitStyles();

            float scale = Mathf.Clamp(Screen.height / 1080f, 0.75f, 2.5f);
            int fontSize = Mathf.RoundToInt(15 * scale);
            _titleStyle.fontSize = Mathf.RoundToInt(17 * scale);
            _textStyle.fontSize = fontSize;
            _smallStyle.fontSize = Mathf.RoundToInt(13 * scale);
            _labelTitle.fontSize = Mathf.RoundToInt(14 * scale);
            _labelSub.fontSize = Mathf.RoundToInt(12 * scale);

            if (ShowSpawners.Value) DrawMarkers();
            if (ShowLegend.Value) DrawLegend(scale);
        }

        private void DrawLegend(float scale)
        {
            float w = 290f * scale;
            float line = 22f * scale;
            var rows = new List<LegendRow>();
            rows.Add(new LegendRow(ColorHostileNow.Value, "Hostiles can spawn now"));
            if (ShowHostileLater.Value) rows.Add(new LegendRow(ColorHostileLater.Value, "Hostiles at night / in weather"));
            if (ShowPassive.Value) rows.Add(new LegendRow(ColorPassive.Value, "Passive animals only"));
            if (ShowProtected.Value) rows.Add(new LegendRow(ColorProtected.Value, "Protected by your base"));

            var creatures = _scanner.Creatures.Values
                .OrderByDescending(c => c.Hostile).ThenByDescending(c => c.Now).ThenBy(c => c.Name)
                .Take(10).ToList();

            float h = 20f * scale            // padding
                      + line * 1.2f          // title
                      + line * rows.Count
                      + (ShowRings.Value ? line : 0f)
                      + line                 // time
                      + (creatures.Count > 0 ? line * (creatures.Count + 1.3f) : line);

            Vector2 pos = LegendPosition.Value;
            var rect = new Rect(Screen.width * pos.x, Screen.height * pos.y, w, h);
            rect.x = Mathf.Clamp(rect.x, 0, Screen.width - w);
            rect.y = Mathf.Clamp(rect.y, 0, Screen.height - h);

            GUI.Box(rect, GUIContent.none, _panelStyle);
            float x = rect.x + 12f * scale, y = rect.y + 10f * scale, iw = w - 24f * scale;

            GUI.Label(new Rect(x, y, iw, line * 1.2f), $"Spawn Visualiser  <size={Mathf.RoundToInt(12 * scale)}>[{ToggleKey.Value}]</size>", _titleStyle);
            y += line * 1.2f;

            float sw = 14f * scale;
            foreach (var row in rows)
            {
                Color c = row.Color; c.a = Mathf.Max(0.75f, c.a);
                DrawSwatch(new Rect(x, y + (line - sw) * 0.5f, sw, sw), c);
                GUI.Label(new Rect(x + sw + 8f * scale, y, iw - sw, line), row.Text, _textStyle);
                y += line;
            }

            if (ShowRings.Value)
            {
                DrawSwatch(new Rect(x, y + line * 0.5f - 1.5f * scale, sw, 3f * scale), Color.white);
                GUI.Label(new Rect(x + sw + 8f * scale, y, iw - sw, line), "Rings: 40 m safe zone / 80 m", _textStyle);
                y += line;
            }

            string time = EnvMan.IsNight() ? "Night" : "Day";
            string env = EnvMan.instance != null ? EnvMan.instance.GetCurrentEnvironment()?.m_name : null;
            bool raid = RandEventSystem.instance?.GetCurrentSpawners() != null;
            GUI.Label(new Rect(x, y, iw, line), $"{time}{(string.IsNullOrEmpty(env) ? "" : " · " + env)}{(raid ? " · <color=#ff6655>RAID</color>" : "")}", _smallStyle);
            y += line;

            if (creatures.Count == 0)
            {
                GUI.Label(new Rect(x, y, iw, line), _scanner.Version == 0 ? "Scanning…" : "Nothing can spawn nearby", _smallStyle);
                return;
            }

            y += line * 0.3f;
            GUI.Label(new Rect(x, y, iw, line), "Can spawn nearby:", _textStyle);
            y += line;
            foreach (CreatureInfo c in creatures)
            {
                Color col = !c.Hostile ? ColorPassive.Value : c.Now ? ColorHostileNow.Value : ColorHostileLater.Value;
                col.a = 1f;
                string suffix = c.Event ? " (raid)" : c.Hostile && !c.Now ? " (later)" : "";
                DrawSwatch(new Rect(x + 4f * scale, y + line * 0.5f - 3f * scale, 6f * scale, 6f * scale), col);
                GUI.Label(new Rect(x + 16f * scale, y, iw, line), c.Name + $"<color=#aaa49c>{suffix}</color>", _textStyle);
                y += line;
            }
        }

        private struct LegendRow
        {
            public readonly Color Color;
            public readonly string Text;
            public LegendRow(Color color, string text) { Color = color; Text = text; }
        }

        private void DrawSwatch(Rect r, Color c)
        {
            Color prev = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, _whiteTex);
            GUI.color = prev;
        }

        private void DrawMarkers()
        {
            Camera cam = GameCamera.instance != null ? GameCamera.instance.GetComponent<Camera>() : Camera.main;
            if (cam == null) return;
            Vector3 playerPos = Player.m_localPlayer.transform.position;

            foreach (SpawnerMarker m in _markers)
            {
                if (m.Transform == null) continue;
                Vector3 world = m.Transform.position + Vector3.up * 2.5f;
                Vector3 sp = cam.WorldToScreenPoint(world);
                if (sp.z <= 0f) continue;
                float y = Screen.height - sp.y;
                float dist = Vector3.Distance(playerPos, m.Transform.position);
                float alpha = Mathf.Clamp01(1.2f - dist / (ScanRadius.Value + 16f));
                if (alpha <= 0.05f) continue;

                Color tc = m.Hostile ? new Color(1f, 0.45f, 0.4f, alpha) : new Color(0.55f, 0.8f, 1f, alpha);
                _labelTitle.normal.textColor = tc;
                _labelSub.normal.textColor = new Color(0.95f, 0.93f, 0.9f, alpha * 0.9f);

                float w = 360f, lh = _labelTitle.fontSize * 1.5f;
                DrawShadowed(new Rect(sp.x - w / 2, y - lh, w, lh), $"◆ {m.Title}  <size={_labelSub.fontSize}>{dist:0} m</size>", _labelTitle);
                DrawShadowed(new Rect(sp.x - w / 2, y, w, lh), m.Subtitle, _labelSub);
            }
        }

        private static void DrawShadowed(Rect r, string text, GUIStyle style)
        {
            Color c = style.normal.textColor;
            style.normal.textColor = new Color(0, 0, 0, c.a * 0.8f);
            GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), text, style);
            style.normal.textColor = c;
            GUI.Label(r, text, style);
        }
    }
}
