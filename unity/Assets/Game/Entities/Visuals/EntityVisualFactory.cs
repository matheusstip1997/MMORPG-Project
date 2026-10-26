#nullable enable
using Shared.Data.Tables;
using UnityEngine;

namespace Game.Gameplay.Entities.Visuals
{
    /// <summary>
    /// Builds an <see cref="EntityVisual"/> from an <see cref="EntityVisualDef"/>: instantiates the body prefab under the
    /// entity's root with the row's uniform scale and hangs the weapons from their humanoid bones. Waits (returns
    /// <see cref="PrefabState.Loading"/>) until every prefab of the row has finished loading. A missing body means the
    /// capsule; a missing weapon or bone only drops that weapon, with a warning.
    /// </summary>
    public sealed class EntityVisualFactory
    {
        private readonly IVisualPrefabSource _source;
        private readonly float _tickMs;

        public EntityVisualFactory(IVisualPrefabSource source, float tickMs)
        {
            _source = source;
            _tickMs = tickMs;
        }

        public IVisualPrefabSource Source => _source;

        /// <summary>Starts loading every address now so views are ready by the time entities spawn.</summary>
        public void Preload(EntityVisualCatalog catalog)
        {
            foreach (var address in catalog.Addresses())
                _source.Get(address, out _);
        }

        public PrefabState TryCreate(EntityVisualDef def, Transform parent, out EntityVisual? visual)
        {
            visual = null;
            var body = _source.Get(def.PrefabAddress, out var bodyPrefab);
            var weapon = Attachment(def.WeaponAddress, out var weaponPrefab);
            var offHand = Attachment(def.OffHandAddress, out var offHandPrefab);
            if (body == PrefabState.Loading || weapon == PrefabState.Loading || offHand == PrefabState.Loading)
                return PrefabState.Loading;
            if (body == PrefabState.Missing || bodyPrefab == null)
                return PrefabState.Missing;

            var instance = Object.Instantiate(bodyPrefab, parent, false);
            instance.name = def.Id;
            float scale = def.ScalePercent / 100f;
            instance.transform.localScale = Vector3.one * scale;
            var rig = instance.GetComponent<EntityVisualRig>();
            var animator = rig != null ? rig.Animator : instance.GetComponentInChildren<Animator>();
            AttachOrWarn(def, animator, def.WeaponBone, weaponPrefab);
            AttachOrWarn(def, animator, def.OffHandBone, offHandPrefab);
            visual = new EntityVisual(instance, _tickMs, scale);
            return PrefabState.Ready;
        }

        private PrefabState Attachment(string address, out GameObject? prefab)
        {
            prefab = null;
            return address.Length == 0 ? PrefabState.Ready : _source.Get(address, out prefab);
        }

        private static void AttachOrWarn(EntityVisualDef def, Animator? animator, VisualBone bone, GameObject? prefab)
        {
            if (bone == VisualBone.None)
                return;
            if (prefab == null)
            {
                Debug.LogWarning($"[Visuals] {def.Id}: the {bone} prefab is missing; shown without it.");
                return;
            }
            if (VisualAttachment.Attach(animator, bone, prefab) == null)
                Debug.LogWarning($"[Visuals] {def.Id}: the body has no humanoid {bone} bone; {prefab.name} is not attached.");
        }
    }

    /// <summary>Hangs prefabs from humanoid bones.</summary>
    public static class VisualAttachment
    {
        public static HumanBodyBones? ToHumanBone(VisualBone bone) => bone switch
        {
            VisualBone.RightHand => HumanBodyBones.RightHand,
            VisualBone.LeftHand => HumanBodyBones.LeftHand,
            _ => null,
        };

        /// <summary>The bone's transform, or null when the body is not humanoid or lacks it.</summary>
        public static Transform? Bone(Animator? animator, VisualBone bone)
        {
            var human = ToHumanBone(bone);
            if (animator == null || !human.HasValue || !animator.isHuman)
                return null;
            return animator.GetBoneTransform(human.Value);
        }

        /// <summary>
        /// Instantiates <paramref name="prefab"/> as a child of the bone with an identity local pose (the prefab carries
        /// its own grip offset). Null when there is no such bone.
        /// </summary>
        public static GameObject? Attach(Animator? animator, VisualBone bone, GameObject prefab)
        {
            var parent = Bone(animator, bone);
            if (parent == null)
                return null;
            var instance = Object.Instantiate(prefab, parent, false);
            instance.name = prefab.name;
            return instance;
        }
    }
}
