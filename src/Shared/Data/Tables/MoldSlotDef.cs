using System;

namespace Shared.Data.Tables
{
    /// <summary>
    /// One material slot of a <see cref="MoldDef"/> (R-5.3-01; spec 5.3 M3 decision 13, WP-M3-14): without
    /// <see cref="Tags"/> a free slot that takes any forge input (<see cref="MaterialTagDef.IsForgeInput"/>), with tags a
    /// forge input whose <see cref="ItemDef.MaterialTags"/> holds one of them; at tier <see cref="MinTier"/> or above,
    /// <see cref="Count"/> units taken from one stack. Flat rows because config tables hold no object arrays; a mold's slots
    /// are in file order, the order of a forge start's inputs. Every number is a placeholder. Catalog 8.5.
    /// </summary>
    [Table("MoldSlotDef", 1)]
    public sealed class MoldSlotDef : IValidatableRow, ISetValidatableRow
    {
        [TableId] public string Id { get; set; } = string.Empty;

        [Ref(typeof(MoldDef))] public string MoldId { get; set; } = string.Empty;

        /// <summary>Display name of the slot, for example "Material 1" (spec 9.1).</summary>
        [Ref(typeof(UiStrings))] public string NameId { get; set; } = string.Empty;

        /// <summary>The material tags the slot takes (any of them); empty: any forge input (decision 13).</summary>
        [Optional, Ref(typeof(MaterialTagDef))] public string[] Tags { get; set; } = Array.Empty<string>();

        /// <summary>The lowest material tier the slot takes.</summary>
        [Range(1, MaterialTierDef.MaxTier)] public int MinTier { get; set; }

        /// <summary>Units the slot consumes, from one stack.</summary>
        [Range(1, 999)] public int Count { get; set; }

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
            for (int i = 0; i < Tags.Length; i++)
            {
                if (Array.IndexOf(Tags, Tags[i]) != i)
                    errors.Add(nameof(Tags), $"lists '{Tags[i]}' twice");
            }
        }

        /// <summary>
        /// The tier is on the ladder, every tag named is a forge input and a mold has at most
        /// <see cref="ForgeStations.MaxInputs"/> slots.
        /// </summary>
        public void Validate(ConfigSet set, RowErrors errors)
        {
            if (set.TryGet(typeof(MaterialTierDef), out _) && MaterialTierDef.ByTier(set, MinTier) == null)
                errors.Add(nameof(MinTier), $"{MinTier} is not a MaterialTierDef tier");
            foreach (var tag in Tags)
            {
                if (MaterialTagDef.Find(set, tag) is { ForgeInput: false })
                    errors.Add(nameof(Tags), $"'{tag}' is no forge input (MaterialTagDef.forgeInput false)");
            }
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

        /// <summary>
        /// Whether a material fits the slot: a forge input of a tier at least <see cref="MinTier"/>, holding one of
        /// <see cref="Tags"/> when the slot names any.
        /// </summary>
        public bool Takes(ConfigSet set, ItemDef item)
        {
            if (item.Tier < MinTier || !MaterialTagDef.IsForgeInput(set, item))
                return false;
            if (Tags.Length == 0)
                return true;
            for (int i = 0; i < Tags.Length; i++)
            {
                if (Array.IndexOf(item.MaterialTags, Tags[i]) >= 0)
                    return true;
            }
            return false;
        }
    }
}
