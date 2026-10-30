namespace Shared.Data.Tables
{
    /// <summary>
    /// Gathering and resource nodes (spec 5.2, WP-M3-4): the tool ladder that tool and node tiers stay within (R-5.2-02),
    /// and the node side of the demand loop (R-5.2-03, R-4.2-03): harvest counters per (node type, region) decay and are
    /// normalized like the kill counters, then map through the same <c>spawnDelay</c> curve and <see cref="DemandSettings"/>
    /// clamp to a node's respawn scale. Placeholders in data.
    /// </summary>
    [Table("GatherSettings", 1)]
    [Singleton]
    public sealed class GatherSettings
    {
        /// <summary>Tiers of tools and nodes run from 1 to this (spec 4.1 M3 item model: three material tiers).</summary>
        [Range(1, 16)] public int ToolTierCount { get; set; }

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
