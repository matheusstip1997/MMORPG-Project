using GameServer.Entities;
using GameServer.Events;
using GameServer.Persistence;
using GameServer.Progression;
using GameServer.Tests.Support;
using GameServer.World;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Actions;
using Shared.Simulation.Stats;

namespace GameServer.Tests.Progression;

/// <summary>
/// Exp, level, achievement counters and the rank-up in the world (spec 3.6, WP-M2-3) with the repository tables (all
/// placeholders): a demon (level 1, rank F) gives 20 exp to a level-1 rank F character; the curve needs 172, 276, 392 and
/// 520 exp for levels 1 to 4. Kills come from <see cref="Killed"/> in phase 11, idempotent per event id; level-ups give a
/// skill point each (R-3.5-04) and save at once; the client hears its state after every change; a rank-up reaches the
/// client and the bus only after its save commits (rule 5).
/// </summary>
public class ProgressionWorldTests
{
    private sealed class Fixture
    {
        public Fixture(Action<ConfigSet>? edit = null, bool log = true)
        {
            var tables = TestCombat.Tables();
            edit?.Invoke(tables);
            Rules = ProgressionRules.From(tables)!;
            Bus = new EventBus(1, new FakeClock(), TestPipelineSettings.BusLimits());
            if (log)
            {
                Bus.Subscribe<ExpGained>(Log);
                Bus.Subscribe<LevelUp>(Log);
                Bus.Subscribe<RankChanged>(Log);
            }
            Harness = new WorldHarness(map: TestCombat.OpenMap(TestCombat.MonsterMarker(2_000, 0)),
                events: new EventDispatchHook(Bus), bus: Bus, actions: ActionCatalog.From(tables),
                combat: TestCombat.Rules(tables, TestCombat.Point()), progression: Rules);
        }

        public WorldHarness Harness { get; }
        public EventBus Bus { get; }
        public ProgressionRules Rules { get; }
        public EventLog Log { get; } = new();
        public GameWorld World => Harness.World;
        public ProgressionStore Store => World.Progression!;

        public Monster Monster() => Assert.Single(World.Entities.OfType<Monster>());

        /// <summary>A kill as HitResolver publishes it, delivered in this tick's phase 11; returns its envelope.</summary>
        public EventEnvelope Kill(Player player, int monsterNetId, int victimCharacterId = 0)
        {
            var envelope = Bus.Publish(new Killed(player.Id, 99, victimCharacterId, monsterNetId, 0, 0), player.CharacterId);
            Harness.Ticks(1);
            return envelope;
        }

        /// <summary>The next class 2 batch (every second) and its completion.</summary>
        public void Save()
        {
            Harness.Clock.Advance(1_000);
            Harness.Ticks(2);
        }

        public int NetIdOf(string monsterId) => Assert.Single(World.Combat!.Monsters, m => m.Id == monsterId).NetId;
    }

    internal sealed class EventLog : IEventConsumer<ExpGained>, IEventConsumer<LevelUp>, IEventConsumer<RankChanged>
    {
        public List<object> Events { get; } = [];
        public void Handle(in EventEnvelope envelope, in ExpGained payload) => Events.Add(payload);
        public void Handle(in EventEnvelope envelope, in LevelUp payload) => Events.Add(payload);
        public void Handle(in EventEnvelope envelope, in RankChanged payload) => Events.Add(payload);
    }

    private static string RankUpKey(Player player, int n) => $"rankUp:test-run:{player.Id}:{n}";

    [Fact]
    public void A_new_character_enters_at_level_1_with_one_skill_point_and_its_client_hears_it()
    {
        var f = new Fixture();
        var player = f.Harness.Join(1);
        f.Harness.Ticks(1);

        var state = Assert.Single(f.Harness.Transport.ProgressionFor(1));
        Assert.Equal((1, 0L, 172L, Rank.F, 1, 100, ProgressionCause.Initial),
            (state.Level, state.Exp, state.ExpToNext, state.Rank, state.SkillPoints, state.BandPercent, state.Cause));
        Assert.Equal(Rank.F, player.Combatant!.Rank);
    }

    [Fact]
    public void A_demon_kill_pays_its_base_exp_to_the_killer_and_tells_its_client()
    {
        var f = new Fixture();
        var player = f.Harness.Join(1);

        f.Kill(player, f.NetIdOf("m1.demon"));
        f.Harness.Ticks(1); // phase 10 after the dispatch

        Assert.Equal(new ProgressSnapshot(1, 20, Rank.F, 1), f.Store.Snapshot(player.CharacterId));
        var gained = Assert.Single(f.Log.Events.OfType<ExpGained>());
        Assert.Equal((player.CharacterId, player.Id, 20L, 100, ExpMathPpm), (gained.CharacterId, gained.EntityId, gained.Amount,
            gained.RankPercent, gained.BandPpm));
        var last = f.Harness.Transport.ProgressionFor(1)[^1];
        Assert.Equal((1, 20L, ProgressionCause.Exp), (last.Level, last.Exp, last.Cause));
    }

    private const long ExpMathPpm = Shared.Simulation.Progression.ExpMath.PpmOne;

    [Fact]
    public void The_same_kill_delivered_twice_pays_and_counts_once()
    {
        var f = new Fixture();
        var player = f.Harness.Join(1);
        int demon = f.NetIdOf("m1.demon");
        var envelope = f.Kill(player, demon);

        // The same event again (a replay after a consumer fault, or a bug): ignored by event id.
        var kill = new Killed(player.Id, 99, 0, demon, 0, 0);
        f.World.Exp!.Handle(envelope, kill);
        f.World.Achievements!.Handle(envelope, kill);

        Assert.Equal(20, f.Store.Snapshot(player.CharacterId).Exp);
        Assert.Equal(1, f.Store.KillCount(player.CharacterId, "m1.demon"));
        Assert.Equal(1, f.Store.CounterValue(player.CharacterId, f.Rules.Counter(AchievementCategory.MonsterKills)!));
    }

    [Fact]
    public void One_gain_can_take_several_levels_with_a_skill_point_each_and_one_LevelUp_event()
    {
        var f = new Fixture();
        var player = f.Harness.Join(1);

        int gained = f.World.Exp!.Grant(player.CharacterId, player.Id, 172 + 276 + 10);
        f.Harness.Ticks(1);

        Assert.Equal(2, gained);
        Assert.Equal(new ProgressSnapshot(3, 10, Rank.F, 3), f.Store.Snapshot(player.CharacterId));
        var levelUp = Assert.Single(f.Log.Events.OfType<LevelUp>());
        Assert.Equal((1, 3), (levelUp.FromLevel, levelUp.ToLevel));
        var last = f.Harness.Transport.ProgressionFor(1)[^1];
        Assert.Equal((3, 10L, 392L, 3, ProgressionCause.LevelUp), (last.Level, last.Exp, last.ExpToNext, last.SkillPoints, last.Cause));
    }

    [Fact]
    public void A_level_up_saves_at_once_with_the_new_level_exp_and_points()
    {
        var f = new Fixture();
        var player = f.Harness.Join(1);
        int before = f.Harness.Saves.Jobs.Count;

        f.World.Exp!.Grant(player.CharacterId, player.Id, 200);
        f.Harness.Ticks(1); // the save asked for by the level-up goes out at the next scheduler tick

        var job = f.Harness.Saves.Jobs.Skip(before).First(j => j.Reason == SaveReason.LevelUp);
        Assert.Equal((2, 28L, 2), (job.State.Level, job.State.Exp, job.State.SkillPoints));
    }

    [Fact]
    public void Players_killed_and_kills_with_no_character_give_no_exp()
    {
        var f = new Fixture();
        var player = f.Harness.Join(1);
        int demon = f.NetIdOf("m1.demon");

        f.Kill(player, 0, victimCharacterId: 77); // a player victim (R-3.6-01: kill monsters for exp)
        f.Bus.Publish(new Killed(5, 99, 0, demon, 0, 0)); // a monster's kill: no character actor
        f.Harness.Ticks(1);

        Assert.Equal(new ProgressSnapshot(1, 0, Rank.F, 1), f.Store.Snapshot(player.CharacterId));
        Assert.Equal(0, f.Store.CounterValue(player.CharacterId, f.Rules.Counter(AchievementCategory.MonsterKills)!));
    }

    [Fact]
    public void Rank_and_band_modifiers_change_the_exp_of_a_kill()
    {
        // The demon one rank above the character (x1.25); at rank F the band is 0-25, so level 27 is 2 above it (x0.6 x0.6 = x0.36).
        var f = new Fixture(tables =>
        {
            Assert.True(tables.TryGet(typeof(MonsterDef), out var monsters));
            monsters.Rows.Cast<MonsterDef>().Single(m => m.Id == "m1.demon").Rank = Rank.E;
        });
        var player = f.Harness.Join(1);
        f.World.Exp!.Grant(player.CharacterId, player.Id, Enumerable.Range(1, 26).Sum(l => (1 + l) * (80 + 6 * l)));
        Assert.Equal(27, f.Store.Snapshot(player.CharacterId).Level);
        long exp = f.Store.Snapshot(player.CharacterId).Exp;

        f.Kill(player, f.NetIdOf("m1.demon"));

        // 20 x 1.25 x 0.36 = 9
        Assert.Equal(exp + 9, f.Store.Snapshot(player.CharacterId).Exp);
        var gained = f.Log.Events.OfType<ExpGained>().Last();
        Assert.Equal((125, 360_000L), (gained.RankPercent, gained.BandPpm));
        f.Harness.Ticks(1);
        Assert.Equal(36, f.Harness.Transport.ProgressionFor(1)[^1].BandPercent); // the exp note shows x0.36
    }

    [Fact]
    public void Kills_count_per_monster_type_boss_kills_and_types_killed()
    {
        var f = new Fixture();
        var player = f.Harness.Join(1);

        f.Kill(player, f.NetIdOf("m1.demon"));
        f.Kill(player, f.NetIdOf("m1.demon"));
        f.Kill(player, f.NetIdOf("m1.cyclopsBoss"));

        long Counter(AchievementCategory category) => f.Store.CounterValue(player.CharacterId, f.Rules.Counter(category)!);
        Assert.Equal(2, f.Store.KillCount(player.CharacterId, "m1.demon"));
        Assert.Equal(1, f.Store.KillCount(player.CharacterId, "m1.cyclopsBoss"));
        Assert.Equal(3, Counter(AchievementCategory.MonsterKills));
        Assert.Equal(1, Counter(AchievementCategory.BossKills));
        Assert.Equal(2, Counter(AchievementCategory.MonsterTypesKilled));
    }

    [Fact]
    public void Outposts_visited_count_each_outpost_once()
    {
        var f = new Fixture();
        var player = f.Harness.Join(1);
        var achievements = f.World.Achievements!;

        Assert.True(achievements.Record(player.CharacterId, AchievementCategory.OutpostsVisited, "outpost.gate"));
        Assert.False(achievements.Record(player.CharacterId, AchievementCategory.OutpostsVisited, "outpost.gate"));
        Assert.True(achievements.Record(player.CharacterId, AchievementCategory.OutpostsVisited, "outpost.mid"));
        Assert.True(achievements.Record(player.CharacterId, AchievementCategory.RunsClaimed, "run.1"));
        Assert.True(achievements.Record(player.CharacterId, AchievementCategory.RunsClaimed, "run.1")); // a count, not distinct

        Assert.Equal(2, f.Store.CounterValue(player.CharacterId, f.Rules.Counter(AchievementCategory.OutpostsVisited)!));
        Assert.Equal(2, f.Store.CounterValue(player.CharacterId, f.Rules.Counter(AchievementCategory.RunsClaimed)!));
    }

    [Fact]
    public void Counters_and_kill_counts_ride_on_the_save_and_come_back_at_the_next_login()
    {
        var f = new Fixture();
        var player = f.Harness.Join(1);
        f.Kill(player, f.NetIdOf("m1.demon"));
        f.Kill(player, f.NetIdOf("m1.cyclopsBoss"));
        f.World.Achievements!.Record(player.CharacterId, AchievementCategory.OutpostsVisited, "outpost.gate");
        f.Save();

        var batch = f.Harness.Saves.Jobs.Last(j => j.Progression != null).Progression!;
        Assert.Equal(["m1.cyclopsBoss", "m1.demon"], batch.KillCounts.Select(k => k.MonsterDefId));
        Assert.Contains("\"outpostsVisited\":[\"outpost.gate\"]", batch.AchievementCountersJson);

        f.Harness.Leave(1);
        f.Harness.Ticks(3);
        Assert.False(f.Store.IsAttached(player.CharacterId));
        var again = f.Harness.Join(1);

        Assert.Equal(new ProgressSnapshot(2, 48, Rank.F, 2), f.Store.Snapshot(again.CharacterId)); // 20 + 200 (boss) - 172
        Assert.Equal(1, f.Store.KillCount(again.CharacterId, "m1.demon"));
        Assert.Equal(1, f.Store.CounterValue(again.CharacterId, f.Rules.Counter(AchievementCategory.BossKills)!));
        Assert.Equal(1, f.Store.CounterValue(again.CharacterId, f.Rules.Counter(AchievementCategory.OutpostsVisited)!));
    }

    [Fact]
    public void A_rank_up_with_unmet_requirements_is_refused_and_changes_nothing()
    {
        var f = new Fixture();
        var player = f.Harness.Join(1);

        var result = f.World.Ranks!.TryRankUp(player.CharacterId, RankUpKey(player, 1));

        Assert.Equal(new RankUpResult(OutpostReason.RankRequirementsNotMet, Rank.F), result);
        Assert.Equal(Rank.F, f.Store.Snapshot(player.CharacterId).Rank);
        Assert.Null(f.Store.TakeBatch(player.CharacterId, Guid.NewGuid()));
    }

    /// <summary>The placeholder F to E requirements: 60 kills, 2 types, 1 outpost.</summary>
    private static void MeetFRequirements(Fixture f, Player player)
    {
        for (int i = 0; i < 59; i++)
            f.Store.RecordKill(player.CharacterId, "m1.demon", boss: false);
        f.Store.RecordKill(player.CharacterId, "m1.alien", boss: false);
        f.World.Achievements!.Record(player.CharacterId, AchievementCategory.OutpostsVisited, "outpost.gate");
    }

    [Fact]
    public void A_rank_up_applies_at_once_and_is_announced_only_after_its_save_commits()
    {
        var f = new Fixture();
        var player = f.Harness.Join(1);
        MeetFRequirements(f, player);
        f.Harness.Saves.AutoComplete = false;
        string key = RankUpKey(player, 1);

        var result = f.World.Ranks!.TryRankUp(player.CharacterId, key);
        f.Harness.Ticks(2); // the rank-up save is issued, not completed

        Assert.Equal(new RankUpResult(OutpostReason.None, Rank.E), result);
        Assert.Equal(Rank.E, f.Store.Snapshot(player.CharacterId).Rank);
        Assert.Equal(Rank.E, player.Combatant!.Rank); // damage steps 3 and 4 use it at once
        Assert.False(f.World.Ranks.IsCommitted(player.CharacterId, key));
        Assert.Empty(f.Log.Events.OfType<RankChanged>());
        Assert.DoesNotContain(f.Harness.Transport.ProgressionFor(1), s => s.Cause == ProgressionCause.RankUp);
        var job = Assert.Single(f.Harness.Saves.Jobs, j => j.Reason == SaveReason.RankUp);
        var rankUp = Assert.Single(job.Progression!.RankUps);
        Assert.Equal((key, Rank.F, Rank.E), (rankUp.OpKey, rankUp.FromRank, rankUp.ToRank));
        Assert.Equal(Rank.E, job.State.Rank);
        Assert.Contains("\"monsterKills\":60", rankUp.CountersJson); // the counters it was checked against

        f.Harness.Saves.CompleteAll();
        f.Harness.Ticks(2);

        Assert.True(f.World.Ranks.IsCommitted(player.CharacterId, key));
        var changed = Assert.Single(f.Log.Events.OfType<RankChanged>());
        Assert.Equal((player.CharacterId, player.Id, Rank.F, Rank.E), (changed.CharacterId, changed.EntityId, changed.From, changed.To));
        var state = Assert.Single(f.Harness.Transport.ProgressionFor(1), s => s.Cause == ProgressionCause.RankUp);
        Assert.Equal(Rank.E, state.Rank);
        Assert.Single(f.Harness.Characters.RankUps);
    }

    [Fact]
    public void A_repeated_rank_up_key_gets_the_first_answer_and_applies_once()
    {
        var f = new Fixture();
        var player = f.Harness.Join(1);
        MeetFRequirements(f, player);
        string key = RankUpKey(player, 1);

        var first = f.World.Ranks!.TryRankUp(player.CharacterId, key);
        var second = f.World.Ranks.TryRankUp(player.CharacterId, key);
        f.Harness.Ticks(3);

        Assert.Equal(first, second);
        Assert.Equal(Rank.E, f.Store.Snapshot(player.CharacterId).Rank);
        Assert.Single(f.Harness.Characters.RankUps);
        // A new key now checks E's requirements, which are not met.
        Assert.Equal(OutpostReason.RankRequirementsNotMet, f.World.Ranks.TryRankUp(player.CharacterId, RankUpKey(player, 2)).Reason);
    }

    [Fact]
    public void At_the_top_rank_a_rank_up_is_refused_as_maxed()
    {
        var f = new Fixture();
        var player = f.Harness.Join(1);
        f.Store.Detach(player.CharacterId);
        f.Store.Attach(player.CharacterId, new CharacterState(1, 0, "", 0, 0, 0, 0, 1, 1, 1, Rank.SS), ProgressionRecord.Empty);

        Assert.Equal(new RankUpResult(OutpostReason.RankMaxed, Rank.SS), f.World.Ranks!.TryRankUp(player.CharacterId, RankUpKey(player, 1)));
    }

    [Fact]
    public void A_saved_rank_is_the_combatant_rank_at_login()
    {
        var f = new Fixture();
        f.Harness.Characters.Add(accountId: 1, slot: 0, new CharacterState(5, 10, "", 0, 0, 0, 0, 100, 10, 100, Rank.D, 5));

        var player = f.Harness.Join(1);

        Assert.Equal(Rank.D, player.Combatant!.Rank);
        Assert.Equal(new ProgressSnapshot(5, 10, Rank.D, 5), f.Store.Snapshot(player.CharacterId));
    }
}
