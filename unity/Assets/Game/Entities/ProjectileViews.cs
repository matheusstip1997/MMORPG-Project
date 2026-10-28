#nullable enable
using System.Collections.Generic;
using Game.Gameplay.Entities.Visuals;
using Game.Gameplay.Prediction;
using Shared.Data;
using UnityEngine;

namespace Game.Gameplay.Entities
{
    /// <summary>
    /// Projectile views (spec 3.2, backlog D3): one per <see cref="ProjectileTracker"/> projectile, created and destroyed
    /// from its events (spec 9.9) and placed every frame on its deterministic path at the interpolation render time, the
    /// time base of the other remote entities, so an arrow meets its target where the target is drawn. Each view shows
    /// the <see cref="Shared.Data.Tables.EntityVisualDef"/> row of its <c>ProjectileDef.visualId</c> through Addressables
    /// (the same factory as <see cref="EntityViews"/>); while it loads, or when it is missing, a thin placeholder bar.
    /// TODO: draw the local player's own projectiles on the predicted tick instead of the render tick.
    /// </summary>
    public sealed class ProjectileViews : MonoBehaviour
    {
        private static readonly int BaseColor = Shader.PropertyToID("_BaseColor");

        [Tooltip("Height of the placeholder bar above the projectile's origin height, in cm (the prefab carries its own).")]
        [SerializeField] private float placeholderHeightCm = 120f;

        [Tooltip("Placeholder colour.")]
        [SerializeField] private Color placeholderColor = new Color(0.95f, 0.85f, 0.4f);

        [Tooltip("How long an explosion burst (ProjectileDef.impactVisualId, WP-M2-9) stays, in seconds (placeholder look).")]
        [SerializeField] private float impactBurstSeconds = 0.4f;

        [Tooltip("Diameter of the placeholder explosion burst, in cm.")]
        [SerializeField] private float impactPlaceholderDiameterCm = 300f;

        private readonly Dictionary<uint, View> _views = new Dictionary<uint, View>();
        private readonly List<Burst> _bursts = new List<Burst>();
        private ProjectileTracker? _tracker;
        private EntityVisualCatalog? _catalog;
        private EntityVisualFactory? _factory;
        private AddressablesPrefabSource? _ownedSource;
        private MaterialPropertyBlock? _block;
        private bool _retryPending;

        private sealed class View
        {
            public View(ClientProjectile projectile, Transform root)
            {
                Projectile = projectile;
                Root = root;
            }

            public ClientProjectile Projectile { get; }
            public Transform Root { get; }
            public EntityVisual? Visual { get; set; }
            public GameObject? Placeholder { get; set; }
            public bool Pending { get; set; }
        }

        private readonly struct Burst
        {
            public Burst(GameObject root, EntityVisual? visual, float endsAt)
            {
                Root = root;
                Visual = visual;
                EndsAt = endsAt;
            }

            public GameObject Root { get; }
            public EntityVisual? Visual { get; }
            public float EndsAt { get; }
        }

        public int Count => _views.Count;

        /// <summary>Explosion bursts still shown (WP-M2-9).</summary>
        public int BurstCount => _bursts.Count;

        /// <summary>Binds to the tracker; with <paramref name="config"/> the arrows show their EntityVisualDef art.</summary>
        public void Bind(ProjectileTracker tracker, ConfigSet? config = null)
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
            foreach (var projectile in tracker.Projectiles.Values)
                OnSpawned(projectile);
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
            foreach (var burst in _bursts)
                Destroy(burst);
            _bursts.Clear();
        }

        /// <summary>Moves every view to its projectile's position at <paramref name="renderTick"/>.</summary>
        public void Place(double renderTick)
        {
            if (_retryPending)
                RetryPending();
            for (int i = _bursts.Count - 1; i >= 0; i--)
            {
                if (Time.time < _bursts[i].EndsAt)
                    continue;
                Destroy(_bursts[i]);
                _bursts.RemoveAt(i);
            }
            foreach (var view in _views.Values)
            {
                var projectile = view.Projectile;
                bool started = renderTick >= projectile.Path.StartTick;
                view.Root.gameObject.SetActive(started);
                if (!started)
                    continue;
                projectile.GetPose(renderTick, out float xCm, out float yCm);
                view.Root.SetPositionAndRotation(WorldUnits.FromGroundCm(xCm, yCm, projectile.HeightCm),
                    WorldUnits.FacingRotation(projectile.Path.Facing));
            }
        }

        public Transform? ViewOf(uint projectileId) => _views.TryGetValue(projectileId, out var view) ? view.Root : null;

        private void OnDestroy()
        {
            Unbind();
            if (_factory != null)
                _factory.Source.Finished -= OnPrefabFinished;
            _ownedSource?.Dispose();
            _ownedSource = null;
        }

        private void OnSpawned(ClientProjectile projectile)
        {
            if (_views.ContainsKey(projectile.Id))
                return;
            var root = new GameObject($"Projectile {projectile.Id}").transform;
            root.SetParent(transform, false);
            root.gameObject.SetActive(false); // until Place puts it on its path
            var view = new View(projectile, root);
            Build(view);
            _views.Add(projectile.Id, view);
        }

        private void OnDespawned(ClientProjectile projectile)
        {
            if (_views.Remove(projectile.Id, out var view))
                Destroy(view);
            // WP-M2-9, the explosion on impact: a burst where it ended (a hit, a wall or its range; not its owner leaving).
            var reason = projectile.Reason;
            if (projectile.Def.ImpactVisualId.Length != 0 && (reason == Shared.Protocol.ProjectileDespawnReason.Hit
                || reason == Shared.Protocol.ProjectileDespawnReason.World || reason == Shared.Protocol.ProjectileDespawnReason.Range))
                ShowBurst(projectile);
        }

        private void ShowBurst(ClientProjectile projectile)
        {
            projectile.GetPose(projectile.EndTick, out float xCm, out float yCm);
            var root = new GameObject($"Impact {projectile.Id}");
            root.transform.SetParent(transform, false);
            root.transform.SetPositionAndRotation(WorldUnits.FromGroundCm(xCm, yCm, projectile.HeightCm),
                WorldUnits.FacingRotation(projectile.Path.Facing));
            EntityVisual? visual = null;
            if (_factory != null && _catalog != null && _catalog.TryGet(projectile.Def.ImpactVisualId, out var def)
                && _factory.TryCreate(def, root.transform, out var created) == PrefabState.Ready)
                visual = created;
            else
                CreateBurstPlaceholder(root.transform);
            _bursts.Add(new Burst(root, visual, Time.time + impactBurstSeconds));
        }

        private void CreateBurstPlaceholder(Transform root)
        {
            _block ??= new MaterialPropertyBlock();
            var ball = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            ball.name = "ImpactPlaceholder";
            VisualObjects.Destroy(ball.GetComponent<Collider>());
            ball.transform.SetParent(root, false);
            float size = impactPlaceholderDiameterCm / WorldUnits.CmPerUnit;
            ball.transform.localScale = new Vector3(size, size * 0.3f, size);
            _block.SetColor(BaseColor, placeholderColor);
            ball.GetComponent<Renderer>().SetPropertyBlock(_block);
        }

        private static void Destroy(in Burst burst)
        {
            burst.Visual?.Dispose();
            if (burst.Root != null)
                VisualObjects.Destroy(burst.Root);
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
            if (_factory != null && _catalog != null && _catalog.TryGet(view.Projectile.Def.VisualId, out var def))
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
                view.Placeholder = CreatePlaceholder(view.Root);
        }

        private GameObject CreatePlaceholder(Transform root)
        {
            _block ??= new MaterialPropertyBlock();
            var bar = GameObject.CreatePrimitive(PrimitiveType.Cube);
            bar.name = "Placeholder";
            VisualObjects.Destroy(bar.GetComponent<Collider>());
            bar.transform.SetParent(root, false);
            bar.transform.localPosition = new Vector3(0f, placeholderHeightCm / WorldUnits.CmPerUnit, 0f);
            bar.transform.localScale = new Vector3(0.05f, 0.05f, 0.6f); // along the facing (+z)
            _block.SetColor(BaseColor, placeholderColor);
            bar.GetComponent<Renderer>().SetPropertyBlock(_block);
            return bar;
        }

        private static void Destroy(View view)
        {
            view.Visual?.Dispose();
            view.Visual = null;
            if (view.Root != null)
                VisualObjects.Destroy(view.Root.gameObject);
        }
    }
}
