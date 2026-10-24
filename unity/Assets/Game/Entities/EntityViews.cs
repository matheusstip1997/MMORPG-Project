#nullable enable
using System.Collections.Generic;
using Game.Gameplay.Entities.Visuals;
using Game.Gameplay.Prediction;
using Shared.Data;
using Shared.Protocol;
using UnityEngine;

namespace Game.Gameplay.Entities
{
    /// <summary>
    /// Entity views (backlog D1, E/F visuals). Views are created and destroyed from the <see cref="EntityRegistry"/>
    /// events (spec 9.9: events, not polling); only transforms and animation are updated every frame.
    /// Each entity shows the <see cref="Shared.Data.Tables.EntityVisualDef"/> row of its visual id, loaded through
    /// Addressables; while a prefab loads, or when the id, the row or the prefab is missing, it shows the capsule
    /// placeholder with a block for its facing. Views implement <see cref="IActionPresentation"/>: the action source
    /// (D2) presents actions on <see cref="LocalPresentation"/> and <see cref="Presentation"/>.
    /// Visual ids: other players and monsters take the <c>visualId</c> of the ClassDef or MonsterDef row their spawn's
    /// definition id names (<see cref="DefinitionVisualIds"/>), or the defaults below when it names none; the local player's
    /// comes from its class id (<see cref="SetLocalVisual"/>; TODO(B4): from the enter-world message).
    /// </summary>
    public sealed class EntityViews : MonoBehaviour
    {
        private static readonly int BaseColor = Shader.PropertyToID("_BaseColor");

        [Tooltip("Visual id of the local player until its class id is known (EntityVisualDef).")]
        [SerializeField] private string localVisualId = "warrior";

        [Tooltip("Visual id of other players whose spawn names no known ClassDef.")]
        [SerializeField] private string playerVisualId = "warrior";

        [Tooltip("Visual id of monsters whose spawn names no known MonsterDef.")]
        [SerializeField] private string monsterVisualId = "demon";

        [Tooltip("Time constant of the measured ground speed that drives the walk/run blend.")]
        [SerializeField] private float speedSmoothingMs = 100f;

        [Tooltip("Measured speeds above this are snaps (corrections, teleports), not movement.")]
        [SerializeField] private float maxMeasuredSpeedCmPerSec = 3000f;

        [Tooltip("Placeholder colour of the local player.")]
        [SerializeField] private Color localColor = new Color(0.2f, 0.6f, 1f);

        [Tooltip("Placeholder colour of other players.")]
        [SerializeField] private Color playerColor = new Color(1f, 0.55f, 0.2f);

        [Tooltip("Placeholder colour of other entities (monsters, NPCs, unknown kinds).")]
        [SerializeField] private Color otherColor = new Color(0.8f, 0.25f, 0.25f);

        private readonly Dictionary<uint, EntityView> _views = new Dictionary<uint, EntityView>();
        private readonly HashSet<string> _warned = new HashSet<string>();
        private EntityRegistry? _registry;
        private EntityVisualCatalog? _catalog;
        private DefinitionVisualIds _definitions = DefinitionVisualIds.Empty;
        private EntityVisualFactory? _factory;
        private ActionVfx? _vfx;
        private AddressablesPrefabSource? _ownedSource;
        private MaterialPropertyBlock? _block;
        private EntityView? _local;
        private float _tickMs = 50f;
        private double _localTick;
        private double _renderTick;
        private bool _retryPending;

        public Transform? LocalView => _local?.Root;

        /// <summary>The action effects on screen (null until <see cref="UseVisuals"/>).</summary>
        public ActionVfx? Vfx => _vfx;

        /// <summary>The local player's view (null until it is shown).</summary>
        public IActionPresentation? LocalPresentation => _local;

        /// <summary>A remote entity's view, or null.</summary>
        public IActionPresentation? Presentation(uint entityId) => _views.TryGetValue(entityId, out var view) ? view : null;

        /// <summary>
        /// Binds to the registry. With <paramref name="config"/>, entities show the EntityVisualDef art loaded through
        /// Addressables; without it, capsules only.
        /// </summary>
        public void Bind(EntityRegistry registry, ConfigSet? config = null)
        {
            Unbind();
            if (config != null)
            {
                _ownedSource ??= new AddressablesPrefabSource();
                UseVisuals(EntityVisualCatalog.From(config), _ownedSource, (float)PredictionSettings.From(config).TickMs,
                    ActionVfx.AreaDurations(config));
                UseDefinitions(DefinitionVisualIds.From(config));
            }
            _registry = registry;
            registry.Spawned += OnSpawned;
            registry.Despawned += OnDespawned;
            foreach (var entity in registry.Entities.Values)
                OnSpawned(entity);
        }

        /// <summary>
        /// Uses <paramref name="source"/> for prefabs (tests pass an in-memory one) and preloads the catalog. Action effects
        /// (<see cref="ActionVfx"/>) last <paramref name="areaDurationMs"/> for actions with a ground area.
        /// </summary>
        public void UseVisuals(EntityVisualCatalog catalog, IVisualPrefabSource source, float tickMs,
            Dictionary<string, int>? areaDurationMs = null)
        {
            if (_factory != null)
                _factory.Source.Finished -= OnPrefabFinished;
            _catalog = catalog;
            _tickMs = tickMs;
            _factory = new EntityVisualFactory(source, tickMs);
            source.Finished += OnPrefabFinished;
            _factory.Preload(catalog);
            _vfx?.Clear();
            _vfx = new ActionVfx(catalog, source, transform, areaDurationMs ?? new Dictionary<string, int>());
            if (_local != null)
                _local.Vfx = _vfx;
            foreach (var view in _views.Values)
                view.Vfx = _vfx;
        }

        /// <summary>Which visual a spawn's definition id names; call before <see cref="Bind"/> (views pick it at spawn).</summary>
        public void UseDefinitions(DefinitionVisualIds definitions) => _definitions = definitions;

        public void Unbind()
        {
            if (_registry != null)
            {
                _registry.Spawned -= OnSpawned;
                _registry.Despawned -= OnDespawned;
                _registry = null;
            }
            foreach (var view in _views.Values)
                DestroyView(view);
            _views.Clear();
        }

        /// <summary>The local player's visual id, normally its class id (TODO(B4): from the enter-world message).</summary>
        public void SetLocalVisual(string? visualId)
        {
            localVisualId = visualId ?? string.Empty;
            if (_local != null)
                Rebuild(_local, localVisualId);
        }

        /// <summary>The tick the local player is drawn at, for presentation start ticks (the view advances it itself between calls).</summary>
        public void SyncLocalTick(double tick)
        {
            _localTick = tick;
            _local?.SyncTick(tick);
        }

        public void ShowLocal(bool show)
        {
            if (show && _local == null)
                _local = CreateView("LocalPlayer", localVisualId, EntityKind.Player, localColor);
            else if (!show && _local != null)
            {
                DestroyView(_local);
                _local = null;
            }
        }

        public void PlaceLocal(Vector3 position, Quaternion rotation)
        {
            if (_local != null)
                _local.Root.SetPositionAndRotation(position, rotation);
        }

        /// <summary>Moves every remote view to its interpolated pose at <paramref name="renderTick"/>.</summary>
        public void PlaceRemotes(double renderTick)
        {
            _renderTick = renderTick;
            if (_registry == null)
                return;
            foreach (var entity in _registry.Entities.Values)
            {
                if (!_views.TryGetValue(entity.Id, out var view))
                    continue;
                if (view.Kind != entity.Kind)
                {
                    // First seen in a snapshot (kind None), named by a later spawn.
                    view.Kind = entity.Kind;
                    view.Color = ColorOf(entity.Kind);
                    Rebuild(view, VisualIdOf(entity));
                }
                if (!entity.Samples.TrySample(renderTick, out var pose))
                    continue;
                view.Root.gameObject.SetActive(true);
                view.Root.SetPositionAndRotation(WorldUnits.FromSnapshotCm(pose.XCm, pose.YCm, pose.ZCm), WorldUnits.FacingRotation(pose.Facing));
                entity.Samples.Prune(renderTick);
            }
        }

        private void LateUpdate() => Advance(Time.deltaTime);

        /// <summary>
        /// One frame, after every view was placed: swaps in prefabs that finished loading, measures ground speeds and
        /// advances the animations.
        /// </summary>
        public void Advance(float deltaS)
        {
            _localTick += deltaS * 1000.0 / _tickMs;
            _vfx?.Advance(deltaS);
            if (_retryPending)
                RetryPending();

            float smoothingS = speedSmoothingMs / 1000f;
            _local?.Animate(deltaS, _localTick, smoothingS, maxMeasuredSpeedCmPerSec);
            foreach (var view in _views.Values)
            {
                if (view.Root.gameObject.activeSelf)
                    view.Animate(deltaS, _renderTick, smoothingS, maxMeasuredSpeedCmPerSec);
            }
        }

        private void OnDestroy()
        {
            Unbind();
            if (_local != null)
                DestroyView(_local);
            _local = null;
            if (_factory != null)
                _factory.Source.Finished -= OnPrefabFinished;
            _vfx?.Clear();
            _vfx = null;
            _ownedSource?.Dispose();
            _ownedSource = null;
        }

        private void OnSpawned(RemoteEntity entity)
        {
            if (_views.ContainsKey(entity.Id))
                return;
            var view = CreateView($"{entity.Kind} {entity.Id}", VisualIdOf(entity), entity.Kind, ColorOf(entity.Kind));
            // Hidden until it has a sample to stand on.
            view.Root.gameObject.SetActive(false);
            _views.Add(entity.Id, view);
        }

        private void OnDespawned(RemoteEntity entity)
        {
            if (_views.Remove(entity.Id, out var view))
                DestroyView(view);
        }

        // Prefabs can finish inside a Get call; rebuilding waits for LateUpdate so nothing is re-entered.
        private void OnPrefabFinished(string address) => _retryPending = true;

        private void RetryPending()
        {
            _retryPending = false;
            if (_local != null && _local.Pending)
                BuildVisual(_local);
            foreach (var view in _views.Values)
            {
                if (view.Pending)
                    BuildVisual(view);
            }
        }

        /// <summary>The visual of the spawn's ClassDef or MonsterDef row; the defaults when the spawn named none we know.</summary>
        private string? VisualIdOf(RemoteEntity entity)
        {
            if (_definitions.TryGet(entity.Kind, entity.DefId, out var visualId))
                return visualId;
            return entity.Kind switch
            {
                EntityKind.Player => playerVisualId,
                EntityKind.Monster => monsterVisualId,
                _ => null,
            };
        }

        private Color ColorOf(EntityKind kind) => kind == EntityKind.Player ? playerColor : otherColor;

        private EntityView CreateView(string name, string? visualId, EntityKind kind, Color color)
        {
            var root = new GameObject(name).transform;
            root.SetParent(transform, false);
            var view = new EntityView(root, visualId, kind, color) { Vfx = _vfx };
            BuildVisual(view);
            return view;
        }

        private void Rebuild(EntityView view, string? visualId)
        {
            if (view.VisualId == visualId && (view.Visual != null || view.Pending))
                return;
            view.VisualId = visualId;
            view.SetVisual(null);
            BuildVisual(view);
        }

        private void BuildVisual(EntityView view)
        {
            view.Pending = false;
            var state = PrefabState.Missing;
            if (_factory != null && _catalog != null && _catalog.TryGet(view.VisualId, out var def))
            {
                state = _factory.TryCreate(def, view.Root, out var visual);
                if (state == PrefabState.Ready)
                {
                    view.SetVisual(visual);
                    visual!.SyncTick(view == _local ? _localTick : _renderTick);
                    if (view.Capsule != null)
                    {
                        VisualObjects.Destroy(view.Capsule);
                        view.Capsule = null;
                    }
                    return;
                }
                view.Pending = state == PrefabState.Loading;
            }
            else if (_factory != null && !string.IsNullOrEmpty(view.VisualId) && _warned.Add(view.VisualId!))
            {
                Debug.LogWarning($"[Visuals] no EntityVisualDef row '{view.VisualId}'; showing the capsule placeholder.");
            }
            if (view.Capsule == null)
                view.Capsule = CreateCapsule(view.Root, view.Color);
        }

        private void DestroyView(EntityView view)
        {
            view.SetVisual(null);
            if (view.Root != null)
                VisualObjects.Destroy(view.Root.gameObject);
        }

        private GameObject CreateCapsule(Transform root, Color color)
        {
            _block ??= new MaterialPropertyBlock();
            var capsule = new GameObject("Capsule").transform;
            capsule.SetParent(root, false);

            // A Unity capsule is 2 m tall with its pivot in the middle: lift it so the root is at the feet.
            var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.name = "Body";
            VisualObjects.Destroy(body.GetComponent<Collider>());
            body.transform.SetParent(capsule, false);
            body.transform.localPosition = new Vector3(0f, 1f, 0f);
            body.transform.localScale = new Vector3(0.8f, 1f, 0.8f);
            Tint(body, color);

            var nose = GameObject.CreatePrimitive(PrimitiveType.Cube);
            nose.name = "Facing";
            VisualObjects.Destroy(nose.GetComponent<Collider>());
            nose.transform.SetParent(capsule, false);
            nose.transform.localPosition = new Vector3(0f, 1.4f, 0.45f);
            nose.transform.localScale = new Vector3(0.25f, 0.25f, 0.4f);
            Tint(nose, Color.Lerp(color, Color.white, 0.6f));
            return capsule.gameObject;
        }

        private void Tint(GameObject target, Color color)
        {
            var renderer = target.GetComponent<Renderer>();
            _block!.SetColor(BaseColor, color);
            renderer.SetPropertyBlock(_block);
        }
    }
}
