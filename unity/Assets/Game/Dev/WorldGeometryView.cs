#nullable enable
using System.Collections.Generic;
using Game.Gameplay.Entities;
using Game.Gameplay.Streaming;
using Shared.World;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.Gameplay.Dev
{
    /// <summary>
    /// Draws the exported world the server simulates in the dev scene (QA wave 3 F3): its blocked cells as walls and the
    /// borders between its regions as strips on the ground (<see cref="WorldGeometry"/>). Built at start from the same
    /// files the server and client prediction load (<see cref="ClientWorld"/>), so it never drifts from the server.
    /// Placeholder visuals: the sizes and colors here are look only, never gameplay. The <c>Hotkeys</c> row
    /// <c>devWorldGeometry</c> (F3) shows or hides it.
    /// </summary>
    public sealed class WorldGeometryView : MonoBehaviour
    {
        private static readonly int BaseColor = Shader.PropertyToID("_BaseColor");

        [SerializeField] private string worldId = "m1";
        [SerializeField] private Material? material;
        [SerializeField] private Color wallColor = new Color(0.62f, 0.42f, 0.32f);
        [SerializeField] private Color borderColor = new Color(0.95f, 0.75f, 0.2f);
        [SerializeField] private float wallHeightM = 1.5f;
        [SerializeField] private float borderWidthM = 0.2f;
        [SerializeField] private float borderLiftM = 0.02f;
        [SerializeField] private bool shown = true;

        private GameObject? _walls;
        private GameObject? _borders;
        private DevKey? _key;

        /// <summary>Wall rectangles and border runs drawn (for the QA run and tests).</summary>
        public int WallCount { get; private set; }
        public int BorderCount { get; private set; }

        private void Start()
        {
            // TODO(B4): the world id from the enter-world message instead of the inspector (as PredictionDevClient).
            if (!ClientWorld.TryLoadGrid(worldId, out var grid, out var error) || grid == null)
            {
                Debug.LogError("[PredictionDev] world view: " + error);
                return;
            }
            var rects = WorldGeometry.BlockedRects(grid);
            var edges = WorldGeometry.RegionEdges(grid);
            WallCount = rects.Count;
            BorderCount = edges.Count;
            _walls = Part("World Walls", WallMesh(grid, rects), wallColor);
            _borders = Part("Region Borders", BorderMesh(grid, edges), borderColor);
            if (ClientConfig.TryLoad(out var config, out error))
                _key = DevKey.From(config, Shared.Data.Tables.Hotkeys.DevWorldGeometry);
            else
                Debug.LogError("[PredictionDev] world view: " + error);
            Show(shown);
        }

        private void Update()
        {
            if (_key != null && _key.WasPressedThisFrame())
                Show(!shown);
        }

        private void OnDestroy() => _key?.Dispose();

        private void Show(bool value)
        {
            shown = value;
            if (_walls != null)
                _walls.SetActive(value);
            if (_borders != null)
                _borders.SetActive(value);
        }

        private GameObject Part(string name, Mesh mesh, Color color)
        {
            var part = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
            part.transform.SetParent(transform, false);
            part.GetComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = part.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            var block = new MaterialPropertyBlock();
            block.SetColor(BaseColor, color);
            renderer.SetPropertyBlock(block);
            return part;
        }

        private Mesh WallMesh(WorldGrid grid, List<CellRect> rects)
        {
            var mesh = NewMesh(grid.Width + "x" + grid.Height + " walls");
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var triangles = new List<int>();
            float baseCm = grid.BaseHeightCm;
            foreach (var rect in rects)
            {
                var min = Corner(grid, rect.Column, rect.Row, baseCm);
                var max = Corner(grid, rect.Column + rect.Width, rect.Row + rect.Height, baseCm) + Vector3.up * wallHeightM;
                AddBox(min, max, vertices, normals, triangles);
            }
            return Fill(mesh, vertices, normals, triangles);
        }

        private Mesh BorderMesh(WorldGrid grid, List<CellEdge> edges)
        {
            var mesh = NewMesh(grid.Width + "x" + grid.Height + " region borders");
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var triangles = new List<int>();
            float liftCm = grid.BaseHeightCm + borderLiftM * WorldUnits.CmPerUnit;
            float half = borderWidthM / 2f;
            foreach (var edge in edges)
            {
                Vector3 from, to, side;
                if (edge.Vertical)
                {
                    from = Corner(grid, edge.Line, edge.Start, liftCm);
                    to = Corner(grid, edge.Line, edge.Start + edge.Length, liftCm);
                    side = Vector3.left * half; // keeps the quad's front face up (see AddQuad)
                }
                else
                {
                    from = Corner(grid, edge.Start, edge.Line, liftCm);
                    to = Corner(grid, edge.Start + edge.Length, edge.Line, liftCm);
                    side = Vector3.forward * half;
                }
                AddQuad(from - side, from + side, to + side, to - side, Vector3.up, vertices, normals, triangles);
            }
            return Fill(mesh, vertices, normals, triangles);
        }

        /// <summary>The Unity position of a grid corner (cell boundaries), at <paramref name="heightCm"/>.</summary>
        private static Vector3 Corner(WorldGrid grid, int column, int row, float heightCm) =>
            WorldUnits.FromGroundCm(grid.OriginXCm + column * grid.CellSizeCm, grid.OriginYCm + row * grid.CellSizeCm, heightCm);

        private static Mesh NewMesh(string name) => new Mesh { name = name, indexFormat = IndexFormat.UInt32 };

        private static Mesh Fill(Mesh mesh, List<Vector3> vertices, List<Vector3> normals, List<int> triangles)
        {
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        private static void AddBox(Vector3 min, Vector3 max, List<Vector3> v, List<Vector3> n, List<int> t)
        {
            var a = new Vector3(min.x, min.y, min.z);
            var b = new Vector3(max.x, min.y, min.z);
            var c = new Vector3(max.x, min.y, max.z);
            var d = new Vector3(min.x, min.y, max.z);
            var e = new Vector3(min.x, max.y, min.z);
            var f = new Vector3(max.x, max.y, min.z);
            var g = new Vector3(max.x, max.y, max.z);
            var h = new Vector3(min.x, max.y, max.z);
            AddQuad(e, h, g, f, Vector3.up, v, n, t);
            AddQuad(a, e, f, b, Vector3.back, v, n, t);
            AddQuad(c, g, h, d, Vector3.forward, v, n, t);
            AddQuad(d, h, e, a, Vector3.left, v, n, t);
            AddQuad(b, f, g, c, Vector3.right, v, n, t);
        }

        /// <summary>A quad p0 p1 p2 p3, clockwise seen from the side <paramref name="normal"/> points to (Unity's front face).</summary>
        private static void AddQuad(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Vector3 normal, List<Vector3> v, List<Vector3> n, List<int> t)
        {
            int start = v.Count;
            v.Add(p0);
            v.Add(p1);
            v.Add(p2);
            v.Add(p3);
            for (int i = 0; i < 4; i++)
                n.Add(normal);
            t.Add(start);
            t.Add(start + 1);
            t.Add(start + 2);
            t.Add(start);
            t.Add(start + 2);
            t.Add(start + 3);
        }
    }
}
