using Shared.Data;
using Shared.Data.Tables;

namespace GameServer.Quests;

/// <summary>
/// The outpost quest tables in runtime form (spec 5.5, WP-M2-6): caps, board settings, step bonus, forfeit penalty, the activity
/// kinds the board offers (table order, weight above 0) and the quest item each monster type drops. Built once from validated
/// tables (reload class Restart).
/// </summary>
public sealed class QuestRules
{
    private readonly Dictionary<string, string> _questItemByMonster;

    public QuestRules(QuestCaps caps, QuestBoardSettings board, RunBonus bonus, ForfeitPenalty penalty,
        IReadOnlyList<OutpostQuestDef> kinds, IReadOnlyList<QuestItemDrop> drops)
    {
        Caps = caps;
        Board = board;
        Bonus = bonus;
        Penalty = penalty;
        Kinds = kinds.Where(k => k.Weight > 0).ToList();
        _questItemByMonster = drops.ToDictionary(d => d.MonsterId, d => d.ItemId, StringComparer.Ordinal);
    }

    public QuestCaps Caps { get; }
    public QuestBoardSettings Board { get; }
    public RunBonus Bonus { get; }
    public ForfeitPenalty Penalty { get; }

    /// <summary>The kinds the board may offer, in table order (rule 6).</summary>
    public IReadOnlyList<OutpostQuestDef> Kinds { get; }

    /// <summary>The quest item <paramref name="monsterId"/> drops for a return step; false when it has none.</summary>
    public bool TryGetQuestItem(string monsterId, out string itemId) => _questItemByMonster.TryGetValue(monsterId, out itemId!);

    /// <summary>The step bonus of a run with <paramref name="completedSteps"/> done steps (<c>RunBonus</c>).</summary>
    public int BonusPercent(int completedSteps) =>
        Shared.Simulation.Quests.RunMath.BonusPercent(completedSteps, Bonus.PerStepPercent, Bonus.CapPercent);

    /// <summary>The penalty of a run with <paramref name="forfeitedSteps"/> forfeited steps (<c>ForfeitPenalty</c>).</summary>
    public int PenaltyPercent(int forfeitedSteps) =>
        Shared.Simulation.Quests.RunMath.PenaltyPercent(forfeitedSteps, Penalty.PerForfeitPercent, Penalty.CapPercent);

    /// <summary>From tables that passed validation; null when the quest tables are not loaded (worlds built without them).</summary>
    public static QuestRules? From(ConfigSet set)
    {
        if (!set.TryGet(typeof(QuestCaps), out _) || !set.TryGet(typeof(OutpostQuestDef), out _))
            return null;
        return new QuestRules(set.Single<QuestCaps>(), set.Single<QuestBoardSettings>(), set.Single<RunBonus>(),
            set.Single<ForfeitPenalty>(), Rows<OutpostQuestDef>(set), Rows<QuestItemDrop>(set));
    }

    private static List<T> Rows<T>(ConfigSet set) where T : class
    {
        var rows = new List<T>();
        if (set.TryGet(typeof(T), out var table))
        {
            foreach (var row in table.Rows)
                rows.Add((T)row);
        }
        return rows;
    }
}
