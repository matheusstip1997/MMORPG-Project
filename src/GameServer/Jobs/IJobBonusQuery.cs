using Shared.Simulation.Jobs;

namespace GameServer.Jobs;

/// <summary>
/// The job skill-tree bonuses an activity reads when it starts (spec 5.1 "computed when an activity starts";
/// <c>docs/design/job-trees.md</c> decisions 4 and 10; WP-M3-7): harvesting reads <see cref="JobBonusKind.YieldPercent"/> and
/// <see cref="JobBonusKind.GemFindPercent"/> for <see cref="JobActivity.Harvest"/>; the forge <see cref="JobBonusKind.QualityBonus"/>
/// (craft or refine), <see cref="JobBonusKind.ActionCount"/>, <see cref="JobBonusKind.ConversionOddsPercent"/>,
/// <see cref="JobBonusKind.KeepInputsPercent"/> and <see cref="MoldTierUnlocked"/>; enchanting and gems their odds and cost.
/// <para>
/// A session reads each value once at its start and keeps it to its end (decision 10: learning works any time, and a respec
/// is refused while a session runs, <see cref="IJobActivityGate"/>). The values come from the committed learned nodes only
/// (like <see cref="IJobQuery"/>) and are capped by <c>JobBonusCap</c> (decision 4, architecture rule 2): the cap bounds the
/// job-tree term; a source of the activity's own (a tool's yield bonus, a <c>QualityCaps</c> entry) keeps its own bound.
/// World thread only. Writes go through <see cref="JobService"/> alone (architecture rule 4).
/// </para>
/// </summary>
public interface IJobBonusQuery
{
    /// <summary>
    /// The capped bonus of <paramref name="kind"/> that <paramref name="jobId"/>'s tree gives <paramref name="activity"/>
    /// (0 when the character does not hold the job or learned nothing of that kind for it). The activity matters: the
    /// Jeweler's Gem Cutter feeds refine and its Fine Setting craft, both <see cref="JobBonusKind.QualityBonus"/>.
    /// </summary>
    int Get(long characterId, string jobId, JobBonusKind kind, JobActivity activity);

    /// <summary>
    /// The job's Master Molds node (<see cref="JobBonusKind.UnlockMoldTiers"/>, decision 5) unlocks <b>making</b> its mold
    /// recipes of <paramref name="tier"/>; using a mold needs only its job level. False when not learned.
    /// </summary>
    bool MoldTierUnlocked(long characterId, string jobId, int tier);
}

/// <summary>No job tree bonuses (worlds without the job tables).</summary>
public sealed class NoJobBonuses : IJobBonusQuery
{
    public static readonly NoJobBonuses Instance = new();

    public int Get(long characterId, string jobId, JobBonusKind kind, JobActivity activity) => 0;

    public bool MoldTierUnlocked(long characterId, string jobId, int tier) => false;
}
