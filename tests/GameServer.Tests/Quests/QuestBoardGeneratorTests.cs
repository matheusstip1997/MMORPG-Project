using GameServer.Quests;
using Shared.Data.Tables;
using Shared.Util;

namespace GameServer.Tests.Quests;

/// <summary>
/// The quest board generator as a pure function (R-5.5-01; spec 5.5 "M2 parameters"): deterministic per seed (rule 6), the
/// demand reward percent (R-4.2-03) scales the kill counts and every reward and weights which monster comes up, kinds appear
/// only where a candidate can offer them, and the committed placeholders give kill X between 5 and 20.
/// </summary>
public class QuestBoardGeneratorTests
{
    private static readonly ItemDef Horn = new() { Id = "horn", NetId = 1, Category = ItemCategory.SellOnly, MaxStack = 99 };
    private static readonly ItemDef Sigil = new() { Id = "sigil", NetId = 2, Category = ItemCategory.Quest, MaxStack = 20 };

    private static QuestCandidate Candidate(int netId, int baseExp = 40, bool drops = true, bool questItem = true) =>
        new(new MonsterDef { Id = $"monster{netId}", NetId = netId }, 1, baseExp, drops ? new[] { Horn } : Array.Empty<ItemDef>(),
            questItem ? Sigil : null);

    private static OutpostQuestDef Kind(QuestKind kind, int weight = 10, int min = 5, int max = 20, int perUnit = 50,
        bool scaled = false) => new()
    {
        Id = kind.ToString(), Kind = kind, Weight = weight, CountMin = min, CountMax = max, ExpPercentPerUnit = perUnit,
        DemandScaled = scaled,
    };

    private static readonly OutpostQuestDef[] Placeholders =
    [
        Kind(QuestKind.Kill, 50, 5, 20, 50, scaled: true), Kind(QuestKind.Deliver, 30, 3, 10, 40),
        Kind(QuestKind.ReturnItem, 20, 1, 5, 150),
    ];

    [Fact]
    public void The_same_seed_and_demand_give_the_same_board()
    {
        var candidates = new[] { Candidate(1), Candidate(2) };

        var a = QuestBoardGenerator.Generate(candidates, Placeholders, 6, _ => 100, new Pcg32Rng(42, 3));
        var b = QuestBoardGenerator.Generate(candidates, Placeholders, 6, _ => 100, new Pcg32Rng(42, 3));
        var other = QuestBoardGenerator.Generate(candidates, Placeholders, 6, _ => 100, new Pcg32Rng(42, 4)); // the next epoch

        Assert.Equal(6, a.Count);
        Assert.Equal(a.Select(Key), b.Select(Key));
        Assert.NotEqual(a.Select(Key), other.Select(Key));
        Assert.Equal(Enumerable.Range(0, 6).Select(i => (byte)i), a.Select(o => o.Slot));
    }

    [Fact]
    public void Kill_counts_stay_between_5_and_20_and_follow_the_demand_reward_percent()
    {
        var candidates = new[] { Candidate(1) };
        var kill = new[] { Placeholders[0] };

        for (ulong seed = 0; seed < 50; seed++)
        {
            var neutral = QuestBoardGenerator.Generate(candidates, kill, 6, _ => 100, new Pcg32Rng(seed));
            var wanted = QuestBoardGenerator.Generate(candidates, kill, 6, _ => 150, new Pcg32Rng(seed));
            var hunted = QuestBoardGenerator.Generate(candidates, kill, 6, _ => 50, new Pcg32Rng(seed));
            for (int i = 0; i < 6; i++)
            {
                Assert.InRange(neutral[i].Count, 5, 20);
                Assert.Equal(Math.Clamp(neutral[i].Count * 150 / 100, 5, 20), wanted[i].Count);
                Assert.Equal(Math.Clamp(neutral[i].Count * 50 / 100, 5, 20), hunted[i].Count);
                // The reward: base exp 40 x 50 % per kill x count x the reward percent.
                Assert.Equal(40L * 50 * neutral[i].Count * 100 / 10_000, neutral[i].RewardExp);
                Assert.Equal(40L * 50 * wanted[i].Count * 150 / 10_000, wanted[i].RewardExp);
                Assert.Equal(150, wanted[i].RewardPercent);
            }
        }
    }

    [Fact]
    public void A_count_that_is_not_demand_scaled_keeps_its_draw_but_the_reward_still_follows_demand()
    {
        var candidates = new[] { Candidate(1) };
        var deliver = new[] { Kind(QuestKind.Deliver, min: 3, max: 10, perUnit: 40) };

        var neutral = QuestBoardGenerator.Generate(candidates, deliver, 6, _ => 100, new Pcg32Rng(9));
        var wanted = QuestBoardGenerator.Generate(candidates, deliver, 6, _ => 200, new Pcg32Rng(9));

        Assert.Equal(neutral.Select(o => o.Count), wanted.Select(o => o.Count));
        Assert.Equal(neutral.Select(o => o.RewardExp * 2), wanted.Select(o => o.RewardExp));
        Assert.All(neutral, o => Assert.Same(Horn, o.Item));
    }

    [Fact]
    public void Rarely_hunted_monsters_come_up_more_often()
    {
        var candidates = new[] { Candidate(1), Candidate(2) };
        var kill = new[] { Kind(QuestKind.Kill) };
        int wanted = 0, total = 0;
        for (ulong seed = 0; seed < 200; seed++)
        {
            foreach (var offer in QuestBoardGenerator.Generate(candidates, kill, 6, c => c.Monster.NetId == 2 ? 300 : 50,
                         new Pcg32Rng(seed)))
            {
                total++;
                if (offer.Target.Monster.NetId == 2)
                    wanted++;
            }
        }
        Assert.InRange(wanted * 100 / total, 80, 92); // 300 : 50 is 86 %
    }

    [Fact]
    public void Deliver_and_return_offers_appear_only_for_monsters_that_can_supply_them()
    {
        var candidates = new[] { Candidate(1, drops: false, questItem: false), Candidate(2, drops: true, questItem: false) };

        var board = QuestBoardGenerator.Generate(candidates, Placeholders, 16, _ => 100, new Pcg32Rng(5));

        Assert.DoesNotContain(board, o => o.Kind == QuestKind.ReturnItem);
        Assert.All(board.Where(o => o.Kind == QuestKind.Deliver), o => Assert.Equal(2, o.Target.Monster.NetId));
        Assert.Contains(board, o => o.Kind == QuestKind.Kill && o.Target.Monster.NetId == 1);

        var onlyReturn = QuestBoardGenerator.Generate(candidates, [Kind(QuestKind.ReturnItem)], 6, _ => 100, new Pcg32Rng(5));
        Assert.Empty(onlyReturn); // no monster drops a quest item: nothing to offer
    }

    [Fact]
    public void Return_offers_name_the_monster_s_quest_item()
    {
        var board = QuestBoardGenerator.Generate([Candidate(1)], [Kind(QuestKind.ReturnItem, min: 1, max: 5, perUnit: 150)], 6,
            _ => 100, new Pcg32Rng(1));

        Assert.All(board, o =>
        {
            Assert.Same(Sigil, o.Item);
            Assert.InRange(o.Count, 1, 5);
            Assert.Equal(40L * 150 * o.Count / 100, o.RewardExp);
        });
    }

    [Fact]
    public void A_zone_without_monsters_or_kinds_gets_an_empty_board()
    {
        Assert.Empty(QuestBoardGenerator.Generate([], Placeholders, 6, _ => 100, new Pcg32Rng(1)));
        Assert.Empty(QuestBoardGenerator.Generate([Candidate(1)], [], 6, _ => 100, new Pcg32Rng(1)));
    }

    private static (QuestKind, int, int, long, int) Key(BoardOffer o) => (o.Kind, o.Target.Monster.NetId, o.Count, o.RewardExp, o.Item?.NetId ?? 0);
}
