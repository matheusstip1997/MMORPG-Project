namespace Shared.Data.Tables
{
    /// <summary>
    /// The color a region's walkable ground has on the minimap (spec 9.3 "region tint per ring", WP-M2-10b). Client-only,
    /// placeholders. A region without a row draws <see cref="MinimapSettings.WalkableColor"/>.
    /// </summary>
    [Table("MinimapRegionTint", 1)]
    public sealed class MinimapRegionTint : IValidatableRow
    {
        /// <summary>The region id.</summary>
        [TableId, Ref(typeof(RegionDef))] public string Id { get; set; } = string.Empty;

        /// <summary><c>#RRGGBB</c> or <c>#RRGGBBAA</c>.</summary>
        public string Color { get; set; } = string.Empty;

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
            if (!HudColor.TryParse(Color, out _))
                errors.Add(nameof(Color), $"'{Color}' is not #RRGGBB or #RRGGBBAA");
        }
    }
}
