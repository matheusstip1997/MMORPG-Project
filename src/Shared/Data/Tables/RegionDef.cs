namespace Shared.Data.Tables
{
    /// <summary>
    /// One region of a world (spec 2.3 <c>RegionDef</c>, catalog 8.5). <c>RegionVolume</c>s in Unity name a row by
    /// <see cref="Id"/>; UnityExporter writes the ids into the world's <c>regions.json</c>, and the world loader and
    /// DataValidator check that each exists here. Its flags are the <see cref="RegionDefaults"/> of its area type.
    /// TODO(open): per-region overrides of the defaults (owner guild, fee profile) and the display name shown in the
    /// region header (spec 9) come with the systems that need them.
    /// </summary>
    [Table("RegionDef", 1)]
    public sealed class RegionDef : IValidatableRow, ISetValidatableRow
    {
        /// <summary>Stable id, for example <c>m1.city</c>.</summary>
        [TableId] public string Id { get; set; } = string.Empty;

        [Ref(typeof(RegionDefaults))] public string AreaType { get; set; } = string.Empty;

        /// <summary>
        /// R-2.3-06: the level a player needs to enter this region from a region with a lower <c>minLevel</c> (0 = no gate).
        /// The shared grid collision refuses the step for a lower level (<c>Shared.World.RegionGates</c>), on the server and in
        /// prediction alike; moving inside the region or leaving it is never refused. M2: 10 on the field zone's rings 2 to 4,
        /// the tutorial gate of spec 12.3 (R-12.3-01).
        /// </summary>
        [Optional, Range(0, 10_000)] public int MinLevel { get; set; }

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
        }

        /// <summary>A gate no character can pass is a data error: <see cref="MinLevel"/> is at most the level cap.</summary>
        public void Validate(ConfigSet set, RowErrors errors)
        {
            if (ProgressionSettings.Find(set) is { } progression && MinLevel > progression.MaxLevel)
                errors.Add(nameof(MinLevel), $"{MinLevel} is above ProgressionSettings.maxLevel {progression.MaxLevel}");
        }
    }
}
