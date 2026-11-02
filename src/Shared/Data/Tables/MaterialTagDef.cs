namespace Shared.Data.Tables
{
    /// <summary>
    /// A material tag (spec 4.1 M3 item model): what kind of craft input a material is (ore, ingot, monster drop, gem,
    /// enchant stone...). <see cref="ItemDef.MaterialTags"/> name rows; mold slots may filter on them (<c>MoldDef</c>,
    /// WP-M3-6; a slot without tags takes any forge input, spec 5.3 M3 decision 13). A new tag is a row. Catalog 8.5.
    /// </summary>
    [Table("MaterialTagDef", 1)]
    public sealed class MaterialTagDef : IValidatableRow
    {
        [TableId] public string Id { get; set; } = string.Empty;

        /// <summary>Display name (spec 9.1).</summary>
        [Ref(typeof(UiStrings))] public string NameId { get; set; } = string.Empty;

        /// <summary>
        /// Whether a material with this tag is a forge input (WP-M3-14, decision 13): false for the inputs of other crafts
        /// (rough gems are cut, enchant stones are spent by enchanting), which a free mold slot never takes and which carry
        /// no <see cref="MaterialStatDef"/> rows. A material is a forge input only when every one of its tags is.
        /// </summary>
        [Optional] public bool ForgeInput { get; set; } = true;

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
        }

        public static MaterialTagDef? Find(ConfigSet set, string id) => NetIds.Find<MaterialTagDef>(set, id, row => row.Id);

        /// <summary>
        /// Whether a material is a forge input: a material with a tier whose tags are all forge-input tags (an unknown tag
        /// counts as one; the reference check reports it).
        /// </summary>
        public static bool IsForgeInput(ConfigSet set, ItemDef item)
        {
            if (item.Category != ItemCategory.Material || item.Tier == 0)
                return false;
            foreach (var tag in item.MaterialTags)
            {
                if (Find(set, tag) is { ForgeInput: false })
                    return false;
            }
            return true;
        }
    }
}
