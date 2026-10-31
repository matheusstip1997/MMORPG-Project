namespace Shared.Simulation.Jobs
{
    /// <summary>
    /// A job activity that can pay job XP (spec 5.1 R-5.1-04; WP-M3-1). Config enum (camelCase names in
    /// <c>JobXpWeights</c>) and on the wire: append only, never renumber. Each value is the <c>Activity</c> of one activity
    /// event (<c>Harvested</c>, <c>Refined</c>, ...).
    /// </summary>
    public enum JobActivity : byte
    {
        None = 0,
        Harvest = 1,
        Refine = 2,
        Craft = 3,
        Convert = 4,
        Enchant = 5,
        GemSocket = 6,
        GemRemove = 7,
        /// <summary>A consumable used (no job XP in M3: it has no <c>JobXpWeights</c> row).</summary>
        ItemUse = 8,
    }

    /// <summary>
    /// A character's job slot (R-5.1-03): one main and one secondary job, each written once (user decision 2026-10-30,
    /// spec 5.1). On the wire and stored as a smallint in <c>job_progress.slot</c>: append only.
    /// </summary>
    public enum JobSlot : byte
    {
        None = 0,
        Main = 1,
        Secondary = 2,
    }

    /// <summary>
    /// A craft bonus a job skill tree gives (spec 5.1 R-5.1-04; the job-tree design, docs/design/job-trees.md), read through the
    /// server's <c>IJobBonusQuery.Get</c> with the activity it feeds, capped. Config enum (camelCase names in the job tree's
    /// rows): append only. WP-M3-7 owns it; harvesting (WP-M3-5) and the forge (WP-M3-6) read it (the second to merge keeps one).
    /// </summary>
    public enum JobBonusKind : byte
    {
        None = 0,
        JobXpPercent = 1,
        YieldPercent = 2,
        GemFindPercent = 3,
        QualityBonus = 4,
        ActionCount = 5,
        ConversionOddsPercent = 6,
        KeepInputsPercent = 7,
        EnchantOddsPercent = 8,
        SocketOddsPercent = 9,
        RemovalCostPercent = 10,
        UnlockMoldTiers = 11,
    }
}
