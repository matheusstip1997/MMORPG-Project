namespace Shared.Data.Tables
{
    /// <summary>
    /// A material tag (spec 4.1 M3 item model): what kind of craft input a material is (ore, ingot, grip, gem, enchant
    /// stone...). <see cref="ItemDef.MaterialTags"/> name rows; mold slots filter on them (<c>MoldDef</c>, WP-M3-6). A new
    /// tag is a row. Catalog 8.5.
    /// </summary>
    [Table("MaterialTagDef", 1)]
    public sealed class MaterialTagDef : IValidatableRow
    {
        [TableId] public string Id { get; set; } = string.Empty;

        /// <summary>Display name (spec 9.1).</summary>
        [Ref(typeof(UiStrings))] public string NameId { get; set; } = string.Empty;

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
        }
    }
}
