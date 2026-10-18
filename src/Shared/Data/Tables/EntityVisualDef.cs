namespace Shared.Data.Tables
{
    /// <summary>Humanoid bone a visual attachment hangs from (the client maps it to Unity's humanoid bones).</summary>
    public enum VisualBone
    {
        None = 0,
        RightHand = 1,
        LeftHand = 2,
    }

    /// <summary>
    /// How an entity looks (backlog E/F visuals, spec 11.1: Addressables for content). Client-only presentation data:
    /// the server never reads it. Addresses name Addressables entries, so our own art replaces the M1 placeholders by
    /// pointing the same rows at new prefabs, without code changes. A prefab carries its own animation set (clips are
    /// art data); action timings always come from <c>ActionDef</c>, and visuals follow them.
    /// The binding of an entity to a row is interim: a player's visual id is its class id until <c>ClassDef</c> has a
    /// <c>visualId</c> column (TODO(E)); monsters get theirs through <c>MonsterDef.visualId</c> (TODO(F)).
    /// </summary>
    [Table("EntityVisualDef", 1)]
    public sealed class EntityVisualDef : IValidatableRow
    {
        /// <summary>Stable id, for example <c>warrior</c> or <c>cyclopsBoss</c>.</summary>
        [TableId] public string Id { get; set; } = string.Empty;

        /// <summary>Addressables address of the body prefab.</summary>
        public string PrefabAddress { get; set; } = string.Empty;

        /// <summary>Addressables address of the main weapon prefab; empty for none.</summary>
        public string WeaponAddress { get; set; } = string.Empty;

        /// <summary>Bone the main weapon hangs from; <see cref="VisualBone.None"/> when there is no weapon.</summary>
        public VisualBone WeaponBone { get; set; }

        /// <summary>Addressables address of the off-hand prefab (a shield); empty for none.</summary>
        public string OffHandAddress { get; set; } = string.Empty;

        /// <summary>Bone the off-hand prefab hangs from; <see cref="VisualBone.None"/> when there is none.</summary>
        public VisualBone OffHandBone { get; set; }

        /// <summary>Uniform scale of the whole visual, in percent of the prefab's own size.</summary>
        [Range(10, 1_000)] public int ScalePercent { get; set; }

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
            if (PrefabAddress.Length == 0)
                errors.Add(nameof(PrefabAddress), "must not be empty");
            CheckAddress(errors, nameof(PrefabAddress), PrefabAddress);
            CheckAttachment(errors, nameof(WeaponAddress), WeaponAddress, nameof(WeaponBone), WeaponBone);
            CheckAttachment(errors, nameof(OffHandAddress), OffHandAddress, nameof(OffHandBone), OffHandBone);
            if (WeaponBone != VisualBone.None && WeaponBone == OffHandBone)
                errors.Add(nameof(OffHandBone), "must differ from WeaponBone");
        }

        private static void CheckAttachment(RowErrors errors, string addressField, string address, string boneField, VisualBone bone)
        {
            CheckAddress(errors, addressField, address);
            if (address.Length > 0 && bone == VisualBone.None)
                errors.Add(boneField, $"must name a bone when {addressField} is set");
            else if (address.Length == 0 && bone != VisualBone.None)
                errors.Add(boneField, $"must be none when {addressField} is empty");
        }

        /// <summary>Addresses are paths of letters, digits, '_', '-', '.' and '/', with no empty segment.</summary>
        private static void CheckAddress(RowErrors errors, string field, string address)
        {
            if (address.Length == 0)
                return;
            bool previousSlash = true;
            foreach (char c in address)
            {
                bool slash = c == '/';
                bool allowed = slash || c == '_' || c == '-' || c == '.' || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');
                if (!allowed || (slash && previousSlash))
                {
                    errors.Add(field, $"'{address}' is not a valid address (letters, digits, '_', '-', '.', '/' segments)");
                    return;
                }
                previousSlash = slash;
            }
            if (previousSlash)
                errors.Add(field, $"'{address}' must not end with '/'");
        }
    }
}
