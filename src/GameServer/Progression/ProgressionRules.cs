using Shared.Data;
using Shared.Data.Tables;
using Shared.Simulation.Progression;
using Shared.Simulation.Stats;

namespace GameServer.Progression;

/// <summary>One requirement of a rank-up (R-3.6-04): the counter and the minimum it needs.</summary>
public readonly record struct RankRequirement(AchievementDef Achievement, int Minimum);

/// <summary>
/// The exp, rank and achievement tables in runtime form (spec 3.6, WP-M2-3): the curves (<see cref="ProgressionCurves"/>),
/// the achievement counter of each category, and the requirements of each rank-up. Built once from validated tables
/// (their reload class is Restart). The monsters' levels and exp stay in <c>MonsterDef</c>, read from the world's
/// current <c>CombatRules</c>, so a hot reload of those reaches the next kill.
/// </summary>
public sealed class ProgressionRules
{
    private readonly AchievementDef?[] _byCategory;
    private readonly RankRequirement[][] _requirements;

    private ProgressionRules(ProgressionCurves curves, AchievementDef?[] byCategory, RankRequirement[][] requirements)
    {
        Curves = curves;
        _byCategory = byCategory;
        _requirements = requirements;
    }

    public ProgressionCurves Curves { get; }

    /// <summary>Number of achievement categories (the <see cref="AchievementCategory"/> list).</summary>
    public static int CategoryCount => (int)AchievementCategory.ItemsCrafted + 1;

    /// <summary>The counter of a category; null when the table has none (then nothing counts it).</summary>
    public AchievementDef? Counter(AchievementCategory category) => _byCategory[(int)category];

    /// <summary>What the rank-up from <paramref name="rank"/> needs; empty at the top rank.</summary>
    public IReadOnlyList<RankRequirement> RequirementsFrom(Rank rank) => _requirements[(int)rank];

    /// <summary>From tables that passed validation; null when the progression tables are not loaded (worlds built without them).</summary>
    public static ProgressionRules? From(ConfigSet set)
    {
        if (!set.TryGet(typeof(ProgressionSettings), out _) || !set.TryGet(typeof(AchievementDef), out var achievements)
            || !set.TryGet(typeof(RankRequirements), out var requirements))
            return null;
        var curves = ProgressionCurves.TryBuild(set, out var error)
                     ?? throw new InvalidOperationException("Progression tables are invalid: " + error);
        var byCategory = new AchievementDef?[CategoryCount];
        foreach (var row in achievements.Rows)
        {
            var achievement = (AchievementDef)row;
            byCategory[(int)achievement.Category] = achievement;
        }
        var lists = new List<RankRequirement>[Ranks.Count];
        for (int r = 0; r < lists.Length; r++)
            lists[r] = new List<RankRequirement>();
        foreach (var row in requirements.Rows)
        {
            var requirement = (RankRequirements)row;
            var achievement = AchievementDef.Find(set, requirement.AchievementId)
                              ?? throw new InvalidOperationException($"RankRequirements '{requirement.Id}' names an unknown achievement");
            lists[(int)requirement.FromRank].Add(new RankRequirement(achievement, requirement.Minimum));
        }
        return new ProgressionRules(curves, byCategory, lists.Select(l => l.ToArray()).ToArray());
    }
}
