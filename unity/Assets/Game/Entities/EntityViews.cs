#nullable enable
using System.Collections.Generic;
using Shared.Protocol;
using UnityEngine;

namespace Game.Gameplay.Entities
{
    /// <summary>
    /// Placeholder views (backlog D1): a capsule per entity with a small block showing its facing. Views are created
    /// and destroyed from the <see cref="EntityRegistry"/> events (spec 9.9: events, not polling); only the transforms
    /// are updated every frame, from the interpolated samples.
    /// </summary>
    public sealed class EntityViews : MonoBehaviour
    {
        private static readonly int BaseColor = Shader.PropertyToID("_BaseColor");

        [Tooltip("Placeholder colour of the local player.")]
        [SerializeField] private Color localColor = new Color(0.2f, 0.6f, 1f);

        [Tooltip("Placeholder colour of other players.")]
        [SerializeField] private Color playerColor = new Color(1f, 0.55f, 0.2f);

        [Tooltip("Placeholder colour of other entities (monsters, NPCs, unknown kinds).")]
        [SerializeField] private Color otherColor = new Color(0.8f, 0.25f, 0.25f);

        private readonly Dictionary<uint, Transform> _views = new Dictionary<uint, Transform>();
        private EntityRegistry? _registry;
        private MaterialPropertyBlock? _block;

        public Transform? LocalView { get; private set; }

        public void Bind(EntityRegistry registry)
        {
            Unbind();
            _registry = registry;
            registry.Spawned += OnSpawned;
            registry.Despawned += OnDespawned;
            foreach (var entity in registry.Entities.Values)
                OnSpawned(entity);
        }

        public void Unbind()
        {
            if (_registry != null)
            {
                _registry.Spawned -= OnSpawned;
                _registry.Despawned -= OnDespawned;
                _registry = null;
            }
            foreach (var view in _views.Values)
            {
                if (view != null)
                    Destroy(view.gameObject);
            }
            _views.Clear();
        }

        public void ShowLocal(bool show)
        {
            if (show && LocalView == null)
                LocalView = CreateView("LocalPlayer", localColor);
            else if (!show && LocalView != null)
            {
                Destroy(LocalView.gameObject);
                LocalView = null;
            }
        }

        public void PlaceLocal(Vector3 position, Quaternion rotation)
        {
            if (LocalView != null)
                LocalView.SetPositionAndRotation(position, rotation);
        }

        /// <summary>Moves every remote view to its interpolated pose at <paramref name="renderTick"/>.</summary>
        public void PlaceRemotes(double renderTick)
        {
            if (_registry == null)
                return;
            foreach (var entity in _registry.Entities.Values)
            {
                if (!_views.TryGetValue(entity.Id, out var view) || !entity.Samples.TrySample(renderTick, out var pose))
                    continue;
                view.gameObject.SetActive(true);
                view.SetPositionAndRotation(WorldUnits.FromSnapshotCm(pose.XCm, pose.YCm, pose.ZCm), WorldUnits.FacingRotation(pose.Facing));
                entity.Samples.Prune(renderTick);
            }
        }

        private void OnDestroy() => Unbind();

        private void OnSpawned(RemoteEntity entity)
        {
            if (_views.ContainsKey(entity.Id))
                return;
            var view = CreateView($"{entity.Kind} {entity.Id}", entity.Kind == EntityKind.Player ? playerColor : otherColor);
            // Hidden until it has a sample to stand on.
            view.gameObject.SetActive(false);
            _views.Add(entity.Id, view);
        }

        private void OnDespawned(RemoteEntity entity)
        {
            if (!_views.Remove(entity.Id, out var view))
                return;
            if (view != null)
                Destroy(view.gameObject);
        }

        private Transform CreateView(string name, Color color)
        {
            _block ??= new MaterialPropertyBlock();
            var root = new GameObject(name).transform;
            root.SetParent(transform, false);

            // A Unity capsule is 2 m tall with its pivot in the middle: lift it so the root is at the feet.
            var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.name = "Body";
            Destroy(body.GetComponent<Collider>());
            body.transform.SetParent(root, false);
            body.transform.localPosition = new Vector3(0f, 1f, 0f);
            body.transform.localScale = new Vector3(0.8f, 1f, 0.8f);
            Tint(body, color);

            var nose = GameObject.CreatePrimitive(PrimitiveType.Cube);
            nose.name = "Facing";
            Destroy(nose.GetComponent<Collider>());
            nose.transform.SetParent(root, false);
            nose.transform.localPosition = new Vector3(0f, 1.4f, 0.45f);
            nose.transform.localScale = new Vector3(0.25f, 0.25f, 0.4f);
            Tint(nose, Color.Lerp(color, Color.white, 0.6f));
            return root;
        }

        private void Tint(GameObject target, Color color)
        {
            var renderer = target.GetComponent<Renderer>();
            _block!.SetColor(BaseColor, color);
            renderer.SetPropertyBlock(_block);
        }
    }
}
