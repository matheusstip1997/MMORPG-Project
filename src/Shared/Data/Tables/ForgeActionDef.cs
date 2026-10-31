namespace Shared.Data.Tables
{
    /// <summary>
    /// A forge action (smelt, hit, cool...; R-5.3-01, R-5.3-02; spec 5.3 M3 decision 7, WP-M3-6): each use adds a number
    /// to the quality, drawn by the server between <see cref="QualityMin"/> and <see cref="QualityMax"/> when the session
    /// finishes (R-5.3-03 "some randomness always applies"), the sum capped by the <c>actions</c> row of
    /// <see cref="QualityCaps"/>. A mold lists the actions it allows. Every number is a placeholder. Catalog 8.5.
    /// </summary>
    [Table("ForgeActionDef", 1)]
    public sealed class ForgeActionDef : IValidatableRow, ISetValidatableRow
    {
        [TableId] public string Id { get; set; } = string.Empty;

        /// <summary>Id on the wire (one byte).</summary>
        [Range(1, 255)] public int NetId { get; set; }

        [Ref(typeof(UiStrings))] public string NameId { get; set; } = string.Empty;

        [Range(0, 10_000)] public int QualityMin { get; set; }

        [Range(0, 10_000)] public int QualityMax { get; set; }

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
            if (QualityMax < QualityMin)
                errors.Add(nameof(QualityMax), "must be at least QualityMin");
        }

        public void Validate(ConfigSet set, RowErrors errors) =>
            NetIds.CheckUnique<ForgeActionDef>(set, this, NetId, row => row.NetId, row => row.Id, errors);

        public static ForgeActionDef? Find(ConfigSet set, string id) => NetIds.Find<ForgeActionDef>(set, id, row => row.Id);
    }

    /// <summary>Forge session rules (spec 5.3 M3 decision 7, WP-M3-6). Every number is a placeholder.</summary>
    [Table("ForgeSettings", 1)]
    [Singleton]
    public sealed class ForgeSettings
    {
        /// <summary>Most forge actions one session takes ("up to a placeholder count per session").</summary>
        [Range(1, ForgeStations.MaxSessionActions)] public int MaxActions { get; set; }

        /// <summary>
        /// The highest success percent a conversion reaches with every bonus (R-5.2-04: "never 100%"): a
        /// <see cref="ConversionOdds"/> row plus the job tree's conversionOddsPercent is clamped to it.
        /// </summary>
        [Range(1, 99)] public int MaxConversionPercent { get; set; }

        public static ForgeSettings? Find(ConfigSet set) =>
            set.TryGet(typeof(ForgeSettings), out var table) && table.Rows.Count == 1 ? (ForgeSettings)table.Rows[0] : null;
    }
}
