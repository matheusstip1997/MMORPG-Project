#nullable enable
using System.Collections.Generic;
using Game.Gameplay.Entities.Visuals;
using Game.Gameplay.Prediction;
using Shared.Data;
using Shared.Data.Tables;
using UnityEngine;

namespace Game.Gameplay.Entities
{
    /// <summary>
    /// Ground area views (spec 3.1, backlog D6): one per <see cref="GroundAreaTracker"/> area, created and destroyed from its
    /// events (spec 9.9) and shown while the interpolation render time is inside its span, the time base of the other remote
    /// entities. Each view shows the <see cref="EntityVisualDef"/> row of its effect's <c>visualId</c> through Addressables
    /// (the same factory as <see cref="EntityViews"/>); without one, while it loads, or when it is missing, a flat translucent
    /// placeholder built from the effect's hit shapes (<see cref="GroundAreaMesh"/>), so the placeholder always covers
    /// exactly what the server tests. Swapping the art is data only (placeholder animations and VFX, user decision).
    /// </summary>
    public sealed class GroundAreaViews : MonoBehaviour
    {
        [Tooltip("Placeholder colour of areas that hit enemies (alpha is the transparency).")]
        [SerializeField] private Color damageColor = new Color(1f, 0.45f, 0.1f, 0.35f);

        [Tooltip("Placeholder colour of areas that only heal.")]
        [SerializeField] private Color healColor = new Color(0.3f, 0.9f, 0.4f, 0.35f);

        [Tooltip("Height of the placeholder above the area's origin height, in cm (avoids z-fighting with the ground).")]
        [SerializeField] private float placeholderLiftCm = 3f;

        private readonly Dictionary<uint, View> _views = new Dictionary<uint, View>();
        private GroundAreaTracker? _tracker;
        private EntityVisualCatalog? _catalog;
        private EntityVisualFactory? _factory;
        private AddressablesPrefabSource? _ownedSource;
        private Material? _material;
        private bool _retryPending;

        private sealed class View
        {
            public View(ClientGroundArea area, Transform root)
            {
                Area = area;
                Root = root;
            }

            public ClientGroundArea Area { get; }
            public Transform Root { get; }
            public EntityVisual? Visual { get; set; }
            public GameObject? Placeholder { get; set; }
            public bool Pending { get; set; }
        }

        public int Count => _views.Count;

        /// <summary>Binds to the tracker; with <paramref name="config"/> the areas show their EntityVisualDef art.</summary>
        public void Bind(GroundAreaTracker tracker, ConfigSet? config = null)
        {
            Unbind();
            if (config != null)
            {
                _ownedSource ??= new AddressablesPrefabSource();
                UseVisuals(EntityVisualCatalog.From(config), _ownedSource, (float)PredictionSettings.From(config).TickMs);
            }
            _tracker = tracker;
            tracker.Spawned += OnSpawned;
            tracker.Despawned += OnDespawned;
            foreach (var area in tracker.Areas.Values)
                OnSpawned(area);
        }

        /// <summary>Uses <paramref name="source"/> for prefabs (tests pass an in-memory one).</summary>
        public void UseVisuals(EntityVisualCatalog catalog, IVisualPrefabSource source, float tickMs)
        {
            if (_factory != null)
                _factory.Source.Finished -= OnPrefabFinished;
            _catalog = catalog;
            _factory = new EntityVisualFactory(source, tickMs);
            source.Finished += OnPrefabFinished;
        }

        public void Unbind()
        {
            if (_tracker != null)
            {
                _tracker.Spawned -= OnSpawned;
                _tracker.Despawned -= OnDespawned;
                _tracker = null;
            }
            foreach (var view in _views.Values)
                Destroy(view);
            _views.Clear();
        }

        /// <summary>Shows the areas whose span contains <paramref name="renderTick"/> at their origin and facing.</summary>
        public void Place(double renderTick)
        {
            if (_retryPending)
                RetryPending();
            foreach (var view in _views.Values)
            {
                var area = view.Area;
                bool shown = area.IsShownAt(renderTick);
                view.Root.gameObject.SetActive(shown);
                if (shown)
                    view.Root.SetPositionAndRotation(WorldUnits.FromGroundCm(area.XCm, area.YCm, area.HeightCm),
                        WorldUnits.FacingRotation(area.Facing));
            }
        }

        public Transform? ViewOf(uint areaId) => _views.TryGetValue(areaId, out var view) ? view.Root : null;

        /// <summary>The placeholder of an area, or null when it shows its visual (tests).</summary>
        public GameObject? PlaceholderOf(uint areaId) => _views.TryGetValue(areaId, out var view) ? view.Placeholder : null;

        private void OnDestroy()
        {
            Unbind();
            if (_factory != null)
                _factory.Source.Finished -= OnPrefabFinished;
            _ownedSource?.Dispose();
            _ownedSource = null;
            if (_material != null)
                VisualObjects.Destroy(_material);
        }

        private void OnSpawned(ClientGroundArea area)
        {
            if (_views.ContainsKey(area.Id))
                return;
            var root = new GameObject($"GroundArea {area.Id}").transform;
            root.SetParent(transform, false);
            root.gameObject.SetActive(false); // until Place shows it
            var view = new View(area, root);
            Build(view);
            _views.Add(area.Id, view);
        }

        private void OnDespawned(ClientGroundArea area)
        {
            if (_views.Remove(area.Id, out var view))
                Destroy(view);
        }

        // Prefabs can finish inside a Get call; rebuilding waits for the next Place so nothing is re-entered.
        private void OnPrefabFinished(string address) => _retryPending = true;

        private void RetryPending()
        {
            _retryPending = false;
            foreach (var view in _views.Values)
            {
                if (view.Pending)
                    Build(view);
            }
        }

        private void Build(View view)
        {
            view.Pending = false;
            string visualId = view.Area.Effect.VisualId;
            if (visualId.Length != 0 && _factory != null && _catalog != null && _catalog.TryGet(visualId, out var def))
            {
                var state = _factory.TryCreate(def, view.Root, out var visual);
                if (state == PrefabState.Ready)
                {
                    view.Visual = visual;
                    if (view.Placeholder != null)
                    {
                        VisualObjects.Destroy(view.Placeholder);
                        view.Placeholder = null;
                    }
                    return;
                }
                view.Pending = state == PrefabState.Loading;
            }
            if (view.Placeholder == null)
                view.Placeholder = CreatePlaceholder(view);
        }

        private GameObject CreatePlaceholder(View view)
        {
            var effect = view.Area.Effect;
            var color = effect.HitsEnemies() ? damageColor : healColor;
            var placeholder = new GameObject("Placeholder");
            placeholder.transform.SetParent(view.Root, false);
            placeholder.transform.localPosition = new Vector3(0f, placeholderLiftCm / WorldUnits.CmPerUnit, 0f);
            placeholder.AddComponent<MeshFilter>().sharedMesh = GroundAreaMesh.Build(view.Area.Shapes, color);
            placeholder.AddComponent<MeshRenderer>().sharedMaterial = Material();
            return placeholder;
        }

        /// <summary>An unlit, alpha-blended material that shows the mesh's vertex colours.</summary>
        private Material Material()
        {
            if (_material == null)
                _material = new Material(Shader.Find("Sprites/Default")) { name = "GroundAreaPlaceholder" };
            return _material;
        }

        private static void Destroy(View view)
        {
            view.Visual?.Dispose();
            view.Visual = null;
            if (view.Placeholder != null)
            {
                var filter = view.Placeholder.GetComponent<MeshFilter>();
                if (filter != null && filter.sharedMesh != null)
                    VisualObjects.Destroy(filter.sharedMesh);
            }
            if (view.Root != null)
                VisualObjects.Destroy(view.Root.gameObject);
        }
    }

    /// <summary>
    /// The flat placeholder of a ground area (Unity units, local to the area: +Z along its facing, +X to its right), one
    /// triangle fan per <see cref="HitShapeDef"/> with the sizes the server tests against (spec 3.1 hit shapes): a cone
    /// from its apex, a box from its origin forward, a capsule around its segment (length 0 is a circle). Presentation only.
    /// </summary>
    public static class GroundAreaMesh
    {
        /// <summary>Segments of a full circle.</summary>
        public const int CircleSegments = 48;

        public static Mesh Build(IReadOnlyList<HitShapeDef> shapes, Color color)
        {
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            foreach (var shape in shapes)
                AddShape(shape, vertices, triangles);
            var mesh = new Mesh { name = "GroundAreaPlaceholder" };
            mesh.SetVertices(vertices);
            var colors = new Color[vertices.Count];
            for (int i = 0; i < colors.Length; i++)
                colors[i] = color;
            mesh.SetColors(colors);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        private static void AddShape(HitShapeDef shape, List<Vector3> vertices, List<int> triangles)
        {
            float x = shape.SideOffsetCm / WorldUnits.CmPerUnit;
            float z = shape.ForwardOffsetCm / WorldUnits.CmPerUnit;
            var outline = new List<Vector3>();
            Vector3 center;
            switch (shape.Kind)
            {
                case HitShapeKind.Cone:
                {
                    center = new Vector3(x, 0f, z);
                    float range = shape.RangeCm / WorldUnits.CmPerUnit;
                    float half = Mathf.Clamp(shape.HalfAngleDeg, 1, 180) * Mathf.Deg2Rad;
                    int steps = Mathf.Max(2, Mathf.CeilToInt(CircleSegments * half / Mathf.PI));
                    for (int i = 0; i <= steps; i++)
                    {
                        float angle = -half + 2f * half * i / steps; // 0 is forward, positive to the right
                        outline.Add(center + new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle)) * range);
                    }
                    break;
                }
                case HitShapeKind.Box:
                {
                    float length = shape.LengthCm / WorldUnits.CmPerUnit;
                    float half = shape.WidthCm / 2f / WorldUnits.CmPerUnit;
                    center = new Vector3(x, 0f, z + length / 2f);
                    outline.Add(new Vector3(x - half, 0f, z));
                    outline.Add(new Vector3(x - half, 0f, z + length));
                    outline.Add(new Vector3(x + half, 0f, z + length));
                    outline.Add(new Vector3(x + half, 0f, z));
                    outline.Add(new Vector3(x - half, 0f, z));
                    break;
                }
                default:
                {
                    float length = shape.LengthCm / WorldUnits.CmPerUnit;
                    float radius = shape.RadiusCm / WorldUnits.CmPerUnit;
                    center = new Vector3(x, 0f, z + length / 2f);
                    int half = CircleSegments / 2;
                    for (int i = 0; i <= half; i++) // the far end, from the left side over the front to the right side
                    {
                        float angle = -Mathf.PI / 2f + Mathf.PI * i / half;
                        outline.Add(new Vector3(x + Mathf.Sin(angle) * radius, 0f, z + length + Mathf.Cos(angle) * radius));
                    }
                    for (int i = 0; i <= half; i++) // the near end, from the right side over the back to the left side
                    {
                        float angle = Mathf.PI / 2f + Mathf.PI * i / half;
                        outline.Add(new Vector3(x + Mathf.Sin(angle) * radius, 0f, z + Mathf.Cos(angle) * radius));
                    }
                    outline.Add(outline[0]);
                    break;
                }
            }
            int first = vertices.Count;
            vertices.Add(center);
            vertices.AddRange(outline);
            for (int i = 0; i < outline.Count - 1; i++)
            {
                // Clockwise seen from above (+Y), so the fan faces up in Unity.
                triangles.Add(first);
                triangles.Add(first + 1 + i);
                triangles.Add(first + 2 + i);
            }
        }
    }
}
