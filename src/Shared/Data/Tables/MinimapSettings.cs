namespace Shared.Data.Tables
{
    /// <summary>
    /// The minimap (spec 9.3, 9.12; WP-M2-10b). Client-only display choices, placeholders in data/tables. Its place on screen
    /// is the <see cref="HudLayout"/> row <c>minimap</c>; the markers are <see cref="MinimapMarkerDef"/> rows.
    /// </summary>
    [Table("MinimapSettings", 1)]
    [Singleton]
    public sealed class MinimapSettings : IValidatableRow
    {
        /// <summary>World centimeters per reference pixel (100: the 240 x 184 frame shows 240 x 184 m).</summary>
        [Range(10, 10_000)] public int CmPerPx { get; set; }

        /// <summary>Grid cells per side of one map texel (2 with 50 cm cells: one texel per meter).</summary>
        [Range(1, 16)] public int CellsPerTexel { get; set; }

        /// <summary>Walkable ground of a region without a <see cref="MinimapRegionTint"/> row.</summary>
        public string WalkableColor { get; set; } = string.Empty;

        /// <summary>Ground nobody walks on: ridges, walls, rocks, and outside the world.</summary>
        public string BlockedColor { get; set; } = string.Empty;

        /// <summary>Spawn markers of one monster type closer than this to a camp's first marker belong to that camp.</summary>
        [Range(0, 100_000)] public int CampMergeRadiusCm { get; set; }

        /// <summary>The death spot shows this long after the death, or until the next death.</summary>
        [Range(1_000, 86_400_000)] public int DeathSpotMs { get; set; }

        public void Validate(RowErrors errors)
        {
            if (!HudColor.TryParse(WalkableColor, out _))
                errors.Add(nameof(WalkableColor), $"'{WalkableColor}' is not #RRGGBB or #RRGGBBAA");
            if (!HudColor.TryParse(BlockedColor, out _))
                errors.Add(nameof(BlockedColor), $"'{BlockedColor}' is not #RRGGBB or #RRGGBBAA");
        }

        public static MinimapSettings? Find(ConfigSet set) =>
            set.TryGet(typeof(MinimapSettings), out var table) && table.Rows.Count == 1 ? (MinimapSettings)table.Rows[0] : null;
    }
}
