using Shared.Data.Tables;
using Shared.Simulation.Quests;
using Shared.Util;

namespace GameServer.Quests;

/// <summary>
/// A monster type of an outpost's zone, as the board generator sees it: the region its spawn points are in (the demand reward
/// percent is per monster type and region, R-4.2-03), its base exp, the stackable drops of its loot table that a deliver step may
/// ask for, and the quest item it drops for a return step (null: none).
/// </summary>
public sealed record QuestCandidate(MonsterDef Monster, byte RegionIndex, int BaseExp, IReadOnlyList<ItemDef> DeliverItems,
    ItemDef? QuestItem)
{
    public bool Offers(QuestKind kind) => kind switch
    {
        QuestKind.Kill => true,
        QuestKind.Deliver => DeliverItems.Count > 0,
        QuestKind.ReturnItem => QuestItem != null,
        _ => false,
    };
}

/// <summary>One offer of a generated board: what accepting it would lock (R-5.5-02).</summary>
public sealed record BoardOffer(byte Slot, OutpostQuestDef Def, QuestCandidate Target, ItemDef? Item, int Count, long RewardExp,
    int RewardPercent)
{
    public QuestKind Kind => Def.Kind;
}

/// <summary>
/// The quest board generator (R-5.5-01: server-generated activities, no premade quests; spec 5.5 "M2 parameters"), a pure
/// function of its inputs and the generator's stream (rule 6). For each of <c>offers</c> slots: a kind by
/// <see cref="OutpostQuestDef.Weight"/> among the kinds some candidate can offer, then a candidate weighted by its demand reward
/// percent (rarely hunted monsters come up more often), then for deliver an item of its drops (even odds) and for return its
/// quest item; the count is drawn evenly in the kind's range and, for a demand-scaled kind, scaled by the reward percent and
/// clamped (<see cref="RunMath.ScaledCount"/>); the reward is <see cref="RunMath.StepReward"/>. Duplicate offers may appear.
/// A zone without candidates gets an empty board.
/// </summary>
public static class QuestBoardGenerator
{
    public static List<BoardOffer> Generate(IReadOnlyList<QuestCandidate> candidates, IReadOnlyList<OutpostQuestDef> kinds,
        int offers, Func<QuestCandidate, int> rewardPercent, IRng rng)
    {
        var board = new List<BoardOffer>(offers);
        if (candidates.Count == 0 || kinds.Count == 0)
            return board;
        var percents = new int[candidates.Count];
        for (int i = 0; i < candidates.Count; i++)
            percents[i] = Math.Max(0, rewardPercent(candidates[i]));

        var eligibleKinds = new List<OutpostQuestDef>(kinds.Count);
        foreach (var kind in kinds)
        {
            if (kind.Weight > 0 && candidates.Any(c => c.Offers(kind.Kind)))
                eligibleKinds.Add(kind);
        }
        if (eligibleKinds.Count == 0)
            return board;

        var picks = new List<int>(candidates.Count);
        for (int slot = 0; slot < offers; slot++)
        {
            var def = PickKind(eligibleKinds, rng);
            picks.Clear();
            long totalWeight = 0;
            for (int i = 0; i < candidates.Count; i++)
            {
                if (!candidates[i].Offers(def.Kind))
                    continue;
                picks.Add(i);
                totalWeight += Math.Max(1, percents[i]);
            }
            int chosen = PickWeighted(picks, percents, totalWeight, rng);
            var target = candidates[chosen];
            int percent = percents[chosen];
            ItemDef? item = def.Kind switch
            {
                QuestKind.Deliver => target.DeliverItems[rng.NextInt(target.DeliverItems.Count)],
                QuestKind.ReturnItem => target.QuestItem,
                _ => null,
            };
            int count = rng.NextInt(def.CountMin, def.CountMax + 1);
            if (def.DemandScaled)
                count = RunMath.ScaledCount(count, percent, def.CountMin, def.CountMax);
            long reward = RunMath.StepReward(target.BaseExp, def.ExpPercentPerUnit, count, percent);
            board.Add(new BoardOffer((byte)slot, def, target, item, count, reward, percent));
        }
        return board;
    }

    private static OutpostQuestDef PickKind(List<OutpostQuestDef> kinds, IRng rng)
    {
        int total = 0;
        foreach (var kind in kinds)
            total += kind.Weight;
        int roll = rng.NextInt(total);
        foreach (var kind in kinds)
        {
            if (roll < kind.Weight)
                return kind;
            roll -= kind.Weight;
        }
        return kinds[^1];
    }

    private static int PickWeighted(List<int> picks, int[] percents, long totalWeight, IRng rng)
    {
        // Whole percents clamped by DemandSettings.rewardMaxPercent over a zone's few monster types: far below int.MaxValue.
        long roll = rng.NextInt((int)Math.Min(totalWeight, int.MaxValue));
        foreach (int index in picks)
        {
            long weight = Math.Max(1, percents[index]);
            if (roll < weight)
                return index;
            roll -= weight;
        }
        return picks[^1];
    }
}
