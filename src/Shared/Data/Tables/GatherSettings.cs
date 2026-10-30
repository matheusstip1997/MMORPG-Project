namespace Shared.Data.Tables
{
    /// <summary>
    /// Resource nodes in the demand loop (spec 5.2 R-5.2-03, R-4.2-03; WP-M3-4): harvest counters per (node type, region)
    /// decay and are normalized like the kill counters, then map through the same <c>spawnDelay</c> curve and
    /// <see cref="DemandSettings"/> clamp to a node's respawn scale. Node and tool tiers are on <see cref="MaterialTierDef"/>.
    /// Placeholders in data.
    /// </summary>
    [Table("GatherSettings", 1)]
    [Singleton]
    public sealed class GatherSettings
    {
        /// <summary>Each demand control step keeps this many thousandths of every harvest counter.</summary>
        [Range(0, 1_000)] public int HarvestRetentionPermille { get; set; }

        /// <summary>The decayed harvests of one node type in one region per active player (thousandths) at which the ratio is 1.0.</summary>
        [Range(1, 1_000_000_000)] public int ExpectedHarvestsPerPlayerMilli { get; set; }

        /// <summary>A scaled node respawn delay is never shorter than this (or the node's own delay when that is shorter).</summary>
        [Range(0, 86_400_000)] public int MinRespawnMs { get; set; }

        public static GatherSettings? Find(ConfigSet set) =>
            set.TryGet(typeof(GatherSettings), out var table) && table.Rows.Count == 1 ? (GatherSettings)table.Rows[0] : null;
    }
}
