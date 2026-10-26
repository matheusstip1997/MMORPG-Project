#nullable enable
using System;
using System.Collections.Generic;
using Shared.Data.Tables;
using UnityEngine;

namespace Game.Gameplay.Entities.Visuals
{
    /// <summary>
    /// Action effects on screen (backlog E placeholders): when an entity's action enters its Active phase and the action's
    /// <see cref="ActionClipEntry"/> names a <see cref="ActionClipEntry.vfxId"/>, the <see cref="EntityVisualDef"/> prefab of
    /// that id is placed where the entity stands, turned to its facing, and removed after the entry's
    /// <see cref="ActionClipEntry.vfxLifetimeMs"/>. It stays where it was placed. Only looks: the server resolves every
    /// effect. A prefab still loading is skipped (the catalog is preloaded). Ground areas are not action effects: they have
    /// their own views with the effect's <c>visualId</c> (<c>GroundAreaViews</c>, WP11).
    /// </summary>
    public sealed class ActionVfx
    {
        private readonly EntityVisualCatalog _catalog;
        private readonly IVisualPrefabSource _source;
        private readonly Transform _parent;
        private readonly List<(GameObject Instance, float LeftS)> _live = new List<(GameObject, float)>();

        public ActionVfx(EntityVisualCatalog catalog, IVisualPrefabSource source, Transform parent)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _parent = parent;
        }

        /// <summary>Effects on screen now.</summary>
        public int LiveCount => _live.Count;

        public IEnumerable<GameObject> Live
        {
            get
            {
                foreach (var entry in _live)
                    yield return entry.Instance;
            }
        }

        /// <summary>Shows the entry's effect at <paramref name="at"/>; false when it names none or its prefab is not ready.</summary>
        public bool Play(ActionClipEntry? entry, Transform at)
        {
            if (entry == null || string.IsNullOrEmpty(entry.vfxId) || !_catalog.TryGet(entry.vfxId, out var def))
                return false;
            if (_source.Get(def.PrefabAddress, out var prefab) != PrefabState.Ready || prefab == null)
                return false;
            var instance = UnityEngine.Object.Instantiate(prefab, at.position, at.rotation, _parent);
            instance.name = def.Id;
            instance.transform.localScale = Vector3.one * (def.ScalePercent / 100f);
            _live.Add((instance, entry.vfxLifetimeMs / 1000f));
            return true;
        }

        /// <summary>Ages the effects and removes the ones whose lifetime ran out.</summary>
        public void Advance(float deltaS)
        {
            int kept = 0;
            for (int i = 0; i < _live.Count; i++)
            {
                var (instance, left) = _live[i];
                left -= deltaS;
                if (left <= 0f || instance == null)
                {
                    if (instance != null)
                        VisualObjects.Destroy(instance);
                    continue;
                }
                _live[kept++] = (instance, left);
            }
            _live.RemoveRange(kept, _live.Count - kept);
        }

        public void Clear()
        {
            foreach (var (instance, _) in _live)
            {
                if (instance != null)
                    VisualObjects.Destroy(instance);
            }
            _live.Clear();
        }
    }
}
