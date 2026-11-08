using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace SpawnVisualiser
{
    /// <summary>Draws the scanned cells as a translucent tile mesh draped over the terrain, plus range rings.</summary>
    public class OverlayRenderer
    {
        private const float HeightOffset = 0.12f;
        private const float WaterLevel = 30f;
        private const float TileGap = 0.12f; // fraction of a cell left empty between tiles

        private readonly GameObject _root;
        private readonly Mesh _mesh;
        private readonly MeshRenderer _renderer;
        private readonly LineRenderer _innerRing;
        private readonly LineRenderer _outerRing;
        private readonly Material _material;

        private readonly List<Vector3> _verts = new List<Vector3>();
        private readonly List<Color32> _colors = new List<Color32>();
        private readonly List<int> _tris = new List<int>();
        private Vector3 _lastRingCenter = new Vector3(float.NaN, 0, 0);

        public OverlayRenderer()
        {
            _root = new GameObject("SpawnVisualiser_Overlay");
            Object.DontDestroyOnLoad(_root);

            _material = new Material(FindShader());
            _material.color = Color.white;
            _material.renderQueue = 3100;
            if (_material.HasProperty("_ZWrite")) _material.SetInt("_ZWrite", 0);

            _mesh = new Mesh { name = "SpawnVisualiserMesh", indexFormat = IndexFormat.UInt32 };
            _mesh.MarkDynamic();

            var tiles = new GameObject("Tiles");
            tiles.transform.SetParent(_root.transform, false);
            tiles.AddComponent<MeshFilter>().sharedMesh = _mesh;
            _renderer = tiles.AddComponent<MeshRenderer>();
            _renderer.sharedMaterial = _material;
            _renderer.shadowCastingMode = ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
            _renderer.lightProbeUsage = LightProbeUsage.Off;
            _renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;

            _innerRing = CreateRing("InnerRing", new Color(1f, 1f, 1f, 0.85f), 0.18f);
            _outerRing = CreateRing("OuterRing", new Color(1f, 1f, 1f, 0.35f), 0.12f);

            SetVisible(false);
        }

        private LineRenderer CreateRing(string name, Color color, float width)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_root.transform, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.sharedMaterial = _material;
            lr.useWorldSpace = true;
            lr.loop = true;
            lr.widthMultiplier = width;
            lr.startColor = lr.endColor = color;
            lr.shadowCastingMode = ShadowCastingMode.Off;
            lr.receiveShadows = false;
            lr.numCornerVertices = 2;
            lr.alignment = LineAlignment.View;
            return lr;
        }

        private static Shader FindShader()
        {
            string[] candidates =
            {
                "Sprites/Default",
                "Legacy Shaders/Particles/Alpha Blended",
                "Particles/Standard Unlit",
                "Unlit/Transparent",
                "UI/Default",
            };
            foreach (string name in candidates)
            {
                Shader s = Shader.Find(name);
                if (s != null)
                {
                    Plugin.Log.LogInfo($"Using shader '{name}' for overlay");
                    return s;
                }
            }
            Plugin.Log.LogWarning("No suitable transparent shader found; overlay may look wrong");
            return Shader.Find("Standard");
        }

        public void SetVisible(bool visible)
        {
            if (_root != null) _root.SetActive(visible);
        }

        public void SetRingsVisible(bool visible)
        {
            _innerRing.enabled = visible;
            _outerRing.enabled = visible;
        }

        public void Destroy()
        {
            if (_root != null) Object.Destroy(_root);
            if (_mesh != null) Object.Destroy(_mesh);
            if (_material != null) Object.Destroy(_material);
        }

        public void Rebuild(SpawnScanner scanner, float cellSize)
        {
            _verts.Clear();
            _colors.Clear();
            _tris.Clear();

            Cell[] cells = scanner.Cells;
            float half = cellSize * (1f - TileGap) * 0.5f;
            float radius = scanner.Radius;
            Vector3 center = scanner.Center;
            float opacity = Mathf.Clamp01(Plugin.Opacity.Value);

            for (int i = 0; i < cells.Length; i++)
            {
                Cell c = cells[i];
                if (c.State == CellState.None) continue;
                if (!Plugin.IsStateShown(c.State)) continue;

                Color col = Plugin.ColorFor(c.State);
                // Fade tiles towards the edge of the scan radius for a softer look.
                float dist = Vector2.Distance(new Vector2(c.Position.x, c.Position.z), new Vector2(center.x, center.z));
                float edge = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(radius * 0.75f, radius, dist));
                col.a *= opacity * edge;
                if (col.a <= 0.01f) continue;

                AddTile(c, half, col);
            }

            _mesh.Clear();
            _mesh.SetVertices(_verts);
            _mesh.SetColors(_colors);
            _mesh.SetTriangles(_tris, 0, false);
            _mesh.RecalculateBounds();
        }

        private void AddTile(Cell c, float half, Color32 col)
        {
            Vector3 n = c.Normal;
            if (n.y < 0.2f) n = new Vector3(n.x, 0.2f, n.z).normalized;
            Vector3 p = c.Position + n * HeightOffset;
            if (c.Position.y < WaterLevel)
            {
                // Underwater (ocean/lake spawns): draw on the water surface instead of the seabed.
                n = Vector3.up;
                p = new Vector3(c.Position.x, WaterLevel + 0.25f, c.Position.z);
            }
            int start = _verts.Count;

            // Tilt the tile so it follows the terrain slope (plane through p with normal n).
            AddVert(p, n, -half, -half, col);
            AddVert(p, n, -half, half, col);
            AddVert(p, n, half, half, col);
            AddVert(p, n, half, -half, col);

            _tris.Add(start); _tris.Add(start + 1); _tris.Add(start + 2);
            _tris.Add(start); _tris.Add(start + 2); _tris.Add(start + 3);
        }

        private void AddVert(Vector3 p, Vector3 n, float dx, float dz, Color32 col)
        {
            float dy = -(n.x * dx + n.z * dz) / n.y;
            _verts.Add(new Vector3(p.x + dx, p.y + dy, p.z + dz));
            _colors.Add(col);
        }

        /// <summary>Updates the "no spawn" (40 m) and "spawn band" (80 m) rings that follow the player.</summary>
        public void UpdateRings(Vector3 playerPos)
        {
            if (!_innerRing.enabled) return;
            if (!float.IsNaN(_lastRingCenter.x) && (playerPos - _lastRingCenter).sqrMagnitude < 0.25f) return;
            _lastRingCenter = playerPos;
            DrawRing(_innerRing, playerPos, 40f, 96);
            DrawRing(_outerRing, playerPos, 80f, 160);
        }

        private static void DrawRing(LineRenderer lr, Vector3 center, float radius, int segments)
        {
            ZoneSystem zs = ZoneSystem.instance;
            lr.positionCount = segments;
            for (int i = 0; i < segments; i++)
            {
                float a = i * Mathf.PI * 2f / segments;
                Vector3 p = new Vector3(center.x + Mathf.Cos(a) * radius, center.y, center.z + Mathf.Sin(a) * radius);
                if (zs != null && zs.GetSolidHeight(p, out float h, 1000)) p.y = h;
                p.y = Mathf.Max(p.y, 30f) + 0.4f; // keep above sea level
                lr.SetPosition(i, p);
            }
        }
    }
}
