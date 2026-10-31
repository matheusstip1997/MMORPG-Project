using System;

namespace Shared.Data.Tables
{
    /// <summary>
    /// One material slot of a <see cref="MoldDef"/> (R-5.3-01: "normal sword: 2 materials for the blade, 1 for the grip"):
    /// a material whose <see cref="ItemDef.MaterialTags"/> holds one of <see cref="Tags"/>, at tier <see cref="MinTier"/>
    /// or above, <see cref="Count"/> units taken from one stack. Flat rows because config tables hold no object arrays; a
    /// mold's slots are in file order, the order of a forge start's inputs. Every number is a placeholder. Catalog 8.5.
    /// </summary>
    [Table("MoldSlotDef", 1)]
    public sealed class MoldSlotDef : IValidatableRow, ISetValidatableRow
    {
        [TableId] public string Id { get; set; } = string.Empty;

        [Ref(typeof(MoldDef))] public string MoldId { get; set; } = string.Empty;

        /// <summary>Display name of the slot, for example "Blade" (spec 9.1).</summary>
        [Ref(typeof(UiStrings))] public string NameId { get; set; } = string.Empty;

        /// <summary>The material tags the slot takes (any of them).</summary>
        [Ref(typeof(MaterialTagDef))] public string[] Tags { get; set; } = Array.Empty<string>();

        /// <summary>The lowest material tier the slot takes.</summary>
        [Range(1, MaterialTierDef.MaxTier)] public int MinTier { get; set; }

        /// <summary>Units the slot consumes, from one stack.</summary>
        [Range(1, 999)] public int Count { get; set; }

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
            if (Tags.Length == 0)
                errors.Add(nameof(Tags), "must list at least one material tag");
            for (int i = 0; i < Tags.Length; i++)
            {
                if (Array.IndexOf(Tags, Tags[i]) != i)
                    errors.Add(nameof(Tags), $"lists '{Tags[i]}' twice");
            }
        }

        /// <summary>The tier is on the ladder and a mold has at most <see cref="ForgeStations.MaxInputs"/> slots.</summary>
        public void Validate(ConfigSet set, RowErrors errors)
        {
            if (set.TryGet(typeof(MaterialTierDef), out _) && MaterialTierDef.ByTier(set, MinTier) == null)
                errors.Add(nameof(MinTier), $"{MinTier} is not a MaterialTierDef tier");
            int index = 0;
            foreach (var other in NetIds.Rows<MoldSlotDef>(set))
            {
                if (other.MoldId != MoldId)
                    continue;
                index++;
                if (ReferenceEquals(other, this))
                    break;
            }
            if (index > ForgeStations.MaxInputs)
                errors.Add(nameof(MoldId), $"'{MoldId}' has more than {ForgeStations.MaxInputs} slots");
        }

        /// <summary>Whether a material fits the slot: a tagged material of a tier at least <see cref="MinTier"/>.</summary>
        public bool Takes(ItemDef item)
        {
            if (item.Category != ItemCategory.Material || item.Tier < MinTier)
                return false;
            for (int i = 0; i < Tags.Length; i++)
            {
                if (Array.IndexOf(item.MaterialTags, Tags[i]) >= 0)
                    return true;
            }
            return false;
        }
    }
}
