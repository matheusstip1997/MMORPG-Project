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
    /// What a job skill-tree node raises (spec 5.1 R-5.1-04; <c>docs/design/job-trees.md</c> decision 4; WP-M3-7). Config
    /// enum (camelCase in <c>JobTree</c> and <c>JobBonusCap</c>): append only, never renumber. Each kind is capped on the sum
    /// of its sources (<c>JobBonusCap</c>) and read by the activity that uses it when it starts (<c>IJobBonusQuery</c>).
    /// </summary>
    public enum JobBonusKind : byte
    {
        None = 0,
        /// <summary>Job XP of the node's activities, +N % (read when the XP is applied).</summary>
        JobXpPercent = 1,
        /// <summary>Percent added to a harvest's count (WP-M3-5, <c>HarvestMath.BonusPercent</c>).</summary>
        YieldPercent = 2,
        /// <summary>Percentage points added to a harvest's chance of a gem entry (WP-M3-5, <c>HarvestMath.Roll</c>, decision 13).</summary>
        GemFindPercent = 3,
        /// <summary>The skill-tree term of <c>QualityCalc</c> (R-5.3-02), +N in its units (WP-M3-6).</summary>
        QualityBonus = 4,
        /// <summary>Forge actions allowed per forge session, +N (WP-M3-6).</summary>
        ActionCount = 5,
        /// <summary>The tier conversion success chance (R-5.2-04), +N percentage points (WP-M3-6).</summary>
        ConversionOddsPercent = 6,
        /// <summary>The chance that a failed conversion keeps its inputs, N % (WP-M3-6).</summary>
        KeepInputsPercent = 7,
        /// <summary>Enchant odds toward +1, +N percentage points, within the cap of R-5.3-04 (WP-M3-8).</summary>
        EnchantOddsPercent = 8,
        /// <summary>The gem socket success chance, +N percentage points (WP-M3-9).</summary>
        SocketOddsPercent = 9,
        /// <summary>The gem removal cost, -N % (WP-M3-9).</summary>
        RemovalCostPercent = 10,
        /// <summary>Unlocks making the job's mold recipes of the node's tiers, not using a mold (decision 5; WP-M3-6).</summary>
        UnlockMoldTiers = 11,
    }
}
