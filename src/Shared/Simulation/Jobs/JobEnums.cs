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
}
