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
    public sealed class RegionDef : IValidatableRow
    {
        /// <summary>Stable id, for example <c>m1.city</c>.</summary>
        [TableId] public string Id { get; set; } = string.Empty;

        [Ref(typeof(RegionDefaults))] public string AreaType { get; set; } = string.Empty;

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
        }
    }
}
