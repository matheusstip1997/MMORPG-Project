using System;

namespace Shared.Data.Tables
{
    /// <summary>
    /// The gear rules of a class (spec 4.1 M2 item model, R-3.5-07; catalog 8.5): the weapon types its class line can use
    /// (armor has no class gating) and the common weapon a new character of it starts with, equipped. Every
    /// <see cref="ClassDef"/> row has one. PLACEHOLDER lists for the M2 roots (user decision 2026-10-27): Warrior sword,
    /// Archer bow or dual daggers, Mage staff or wand, Assist staff or gloves. TODO(M2): weapon unlocks by tree nodes
    /// (R-3.5-07) and the tier-1 class lines (WP-M2-5).
    /// </summary>
    [Table("ClassGearDef", 1)]
    public sealed class ClassGearDef : IValidatableRow, ISetValidatableRow
    {
        /// <summary>The class (<see cref="ClassDef"/> id).</summary>
        [TableId, Ref(typeof(ClassDef))] public string Id { get; set; } = string.Empty;

        public WeaponType[] WeaponTypes { get; set; } = Array.Empty<WeaponType>();

        /// <summary>Equipped at the character's first world entry (common rarity); empty for none.</summary>
        [Optional, Ref(typeof(ItemDef), Optional = true)] public string StarterWeaponId { get; set; } = string.Empty;

        public bool CanUse(WeaponType type) => Array.IndexOf(WeaponTypes, type) >= 0;

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
            for (int i = 0; i < WeaponTypes.Length; i++)
            {
                if (WeaponTypes[i] == WeaponType.None || !Enum.IsDefined(typeof(WeaponType), WeaponTypes[i]))
                    errors.Add(nameof(WeaponTypes), $"{(int)WeaponTypes[i]} is not a weapon type");
                else if (Array.IndexOf(WeaponTypes, WeaponTypes[i]) != i)
                    errors.Add(nameof(WeaponTypes), $"lists {WeaponTypes[i]} twice");
            }
        }

        public void Validate(ConfigSet set, RowErrors errors)
        {
            // Every class has a row (checked on the first row only), so equipping a weapon always has an answer.
            ClassGearDef? first = null;
            foreach (var row in NetIds.Rows<ClassGearDef>(set))
            {
                first = row;
                break;
            }
            if (ReferenceEquals(first, this))
            {
                foreach (var cls in NetIds.Rows<ClassDef>(set))
                {
                    if (Find(set, cls.Id) == null)
                        errors.Add(nameof(Id), $"class '{cls.Id}' has no ClassGearDef row (R-3.5-07)");
                }
            }
            if (StarterWeaponId.Length == 0)
                return;
            var starter = ItemDef.Find(set, StarterWeaponId);
            if (starter == null)
                return;
            if (starter.Category != ItemCategory.Equipment || starter.WeaponType == WeaponType.None)
                errors.Add(nameof(StarterWeaponId), $"'{StarterWeaponId}' is not a weapon");
            else if (!CanUse(starter.WeaponType))
                errors.Add(nameof(StarterWeaponId), $"'{StarterWeaponId}' is a {starter.WeaponType}, which the class cannot use");
        }

        public static ClassGearDef? Find(ConfigSet set, string id) => NetIds.Find<ClassGearDef>(set, id, row => row.Id);
    }
}
