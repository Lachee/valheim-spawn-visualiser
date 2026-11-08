using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace SpawnVisualiser
{
    /// <summary>
    /// Draws the scanned cells as translucent tiles draped over the terrain, plus range rings.
    ///
    /// Tiles are snapped to Valheim's 1 m terrain grid and each vertex takes its height straight from the
    /// heightmap, so they hug the ground instead of being flat slabs that bumps poke through.
    /// The mesh is drawn twice: a normal depth-tested pass, and a faint "x-ray" pass (ZTest Greater) so
    /// tiles hidden by grass, rocks or hills are still visible instead of disappearing.
    /// </summary>
    public class OverlayRenderer
    {
        private const float HeightOffset = 0.15f;
        private const float WaterLevel = 30f;
        private const float TileGap = 0.12f;       // fraction of a cell left empty between tiles
        private const int HiddenQueue = 3100;
        private const int VisibleQueue = 3101;

        private readonly GameObject _root;
        private readonly Mesh _mesh;
        private readonly MeshRenderer _renderer;
        private readonly LineRenderer _innerRing;
        private readonly LineRenderer _outerRing;
        private readonly Material _visibleMat;
        private readonly Material _hiddenMat;
        private readonly Material _ringMat;
        private readonly bool _supportsXRay;

        private readonly List<Vector3> _verts = new List<Vector3>();
        private readonly List<Color32> _colors = new List<Color32>();
        private readonly List<int> _tris = new List<int>();
        private Vector3 _lastRingCenter = new Vector3(float.NaN, 0, 0);

        // Terrain height cache for the current rebuild, one entry per whole-metre grid vertex.
        private float[] _heights = new float[0];
        private int _gridVerts;
        private Vector3 _gridMin;

        public OverlayRenderer()
        {
            _root = new GameObject("SpawnVisualiser_Overlay");
            Object.DontDestroyOnLoad(_root);

            Shader colored = Shader.Find("Hidden/Internal-Colored");
            if (colored != null)
            {
                // Internal-Colored exposes its blend/depth state as properties, so we can build both passes from it.
                _supportsXRay = true;
                _visibleMat = MakeColoredMaterial(colored, CompareFunction.LessEqual, VisibleQueue);
                _hiddenMat = MakeColoredMaterial(colored, CompareFunction.Greater, HiddenQueue);
                Plugin.Log.LogInfo("Using shader 'Hidden/Internal-Colored' for overlay (with x-ray pass)");
            }
            else
            {
                _visibleMat = new Material(FindFallbackShader()) { renderQueue = VisibleQueue };
                Plugin.Log.LogWarning("Hidden/Internal-Colored not found; hidden tiles will not be shown");
            }
            _ringMat = new Material(_visibleMat);

            _mesh = new Mesh { name = "SpawnVisualiserMesh", indexFormat = IndexFormat.UInt32 };
            _mesh.MarkDynamic();

            var tiles = new GameObject("Tiles");
            tiles.transform.SetParent(_root.transform, false);
            tiles.AddComponent<MeshFilter>().sharedMesh = _mesh;
            _renderer = tiles.AddComponent<MeshRenderer>();
            // More materials than submeshes: Unity draws the single submesh once per material (multi-pass).
            _renderer.sharedMaterials = _supportsXRay ? new[] { _hiddenMat, _visibleMat } : new[] { _visibleMat };
            _renderer.shadowCastingMode = ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
            _renderer.lightProbeUsage = LightProbeUsage.Off;
            _renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;

            _innerRing = CreateRing("InnerRing", new Color(1f, 1f, 1f, 0.85f), 0.18f);
            _outerRing = CreateRing("OuterRing", new Color(1f, 1f, 1f, 0.35f), 0.12f);

            SetVisible(false);
        }

        private static Material MakeColoredMaterial(Shader shader, CompareFunction zTest, int queue)
        {
            var m = new Material(shader) { hideFlags = HideFlags.HideAndDontSave, renderQueue = queue };
            m.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            m.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            m.SetInt("_Cull", (int)CullMode.Off);
            m.SetInt("_ZWrite", 0);
            m.SetInt("_ZTest", (int)zTest);
            m.SetFloat("_ZBias", -1f);
            m.color = Color.white;
            return m;
        }

        private static Shader FindFallbackShader()
        {
            foreach (string name in new[] { "Sprites/Default", "Legacy Shaders/Particles/Alpha Blended", "UI/Default" })
            {
                Shader s = Shader.Find(name);
                if (s != null) return s;
            }
            return Shader.Find("Standard");
        }

        private LineRenderer CreateRing(string name, Color color, float width)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_root.transform, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.sharedMaterial = _ringMat;
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
            if (_visibleMat != null) Object.Destroy(_visibleMat);
            if (_hiddenMat != null) Object.Destroy(_hiddenMat);
            if (_ringMat != null) Object.Destroy(_ringMat);
        }

        public void Rebuild(SpawnScanner scanner)
        {
            _verts.Clear();
            _colors.Clear();
            _tris.Clear();

            if (_hiddenMat != null)
                _hiddenMat.color = new Color(1f, 1f, 1f, Mathf.Clamp01(Plugin.OccludedOpacity.Value));

            int cs = scanner.CellSize;
            int size = scanner.Size;
            _gridMin = scanner.GridMin;
            _gridVerts = size * cs + 1;
            int n = _gridVerts * _gridVerts;
            if (_heights.Length < n) _heights = new float[n];
            for (int i = 0; i < n; i++) _heights[i] = float.NaN;

            Cell[] cells = scanner.Cells;
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

                AddTile(c, (i % size) * cs, (i / size) * cs, cs, col);
            }

            _mesh.Clear();
            _mesh.SetVertices(_verts);
            _mesh.SetColors(_colors);
            _mesh.SetTriangles(_tris, 0, false);
            _mesh.RecalculateBounds();
        }

        /// <param name="gx">Grid-local X (metres) of the tile's min corner.</param>
        /// <param name="gz">Grid-local Z (metres) of the tile's min corner.</param>
        private void AddTile(Cell c, int gx, int gz, int cs, Color32 col)
        {
            float inset = Mathf.Max(0.08f, cs * TileGap * 0.5f);
            bool water = c.Position.y < WaterLevel;
            // One quad per terrain square so the tile bends exactly with the ground; water is flat.
            int steps = water ? 1 : cs;
            float span = cs - inset * 2f;
            int start = _verts.Count;

            for (int z = 0; z <= steps; z++)
            {
                float lz = gz + inset + span * z / steps;
                for (int x = 0; x <= steps; x++)
                {
                    float lx = gx + inset + span * x / steps;
                    float y = water ? WaterLevel + 0.25f : SampleHeight(lx, lz, c) + HeightOffset;
                    _verts.Add(new Vector3(_gridMin.x + lx, y, _gridMin.z + lz));
                    _colors.Add(col);
                }
            }

            int row = steps + 1;
            for (int z = 0; z < steps; z++)
            {
                for (int x = 0; x < steps; x++)
                {
                    int a = start + z * row + x;
                    int b = a + 1, d = a + row, e = d + 1;
                    _tris.Add(a); _tris.Add(d); _tris.Add(e);
                    _tris.Add(a); _tris.Add(e); _tris.Add(b);
                }
            }
        }

        /// <summary>Bilinear terrain height at a grid-local position, from whole-metre heightmap vertices.</summary>
        private float SampleHeight(float lx, float lz, Cell c)
        {
            int x0 = Mathf.Clamp(Mathf.FloorToInt(lx), 0, _gridVerts - 2);
            int z0 = Mathf.Clamp(Mathf.FloorToInt(lz), 0, _gridVerts - 2);
            float fx = lx - x0, fz = lz - z0;
            float h00 = VertexHeight(x0, z0, c);
            float h10 = VertexHeight(x0 + 1, z0, c);
            float h01 = VertexHeight(x0, z0 + 1, c);
            float h11 = VertexHeight(x0 + 1, z0 + 1, c);
            return Mathf.Lerp(Mathf.Lerp(h00, h10, fx), Mathf.Lerp(h01, h11, fx), fz);
        }

        private float VertexHeight(int x, int z, Cell c)
        {
            int idx = z * _gridVerts + x;
            float h = _heights[idx];
            if (!float.IsNaN(h)) return h;

            var world = new Vector3(_gridMin.x + x, 0f, _gridMin.z + z);
            if (c.Heightmap == null || !c.Heightmap.GetWorldHeight(world, out h))
            {
                // Vertex belongs to a neighbouring zone's heightmap.
                Heightmap hm = Heightmap.FindHeightmap(world);
                if (hm == null || !hm.GetWorldHeight(world, out h))
                    h = c.Position.y;
            }
            _heights[idx] = h;
            return h;
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
                p.y = Mathf.Max(p.y, WaterLevel) + 0.4f; // keep above sea level
                lr.SetPosition(i, p);
            }
        }
    }
}
