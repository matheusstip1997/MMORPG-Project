using GameServer.Entities;
using GameServer.Events;
using GameServer.Progression;
using GameServer.Tests.Support;
using Shared.Protocol;
using Shared.Simulation;
using Shared.Simulation.Actions;
using Shared.Simulation.Stats;

namespace GameServer.Tests.World;

/// <summary>
/// WP-M2-3 "done": killing demons levels a character, through the whole pipeline. The player's normal attack fires its
/// strike (phase 3), HitResolver kills the demon and publishes <see cref="Killed"/> (phase 5), ExpService pays the
/// killer in phase 11, and the client gets its new level in a reliable <see cref="ProgressionState"/>. Repository tables
/// (placeholders): a demon is level 1 and gives 20 exp; levels 1 to 4 need 172 + 276 + 392 + 520 = 1360 exp.
/// </summary>
public class DemonLevelingTests
{
    private const int ExpToLevel5 = 172 + 276 + 392 + 520;
    private const int DemonExp = 20;

    private sealed class Setup
    {
        public Setup()
        {
            var tables = TestCombat.Tables();
            Bus = new EventBus(1, new FakeClock(), TestPipelineSettings.BusLimits());
            Bus.Subscribe(Levels);
            Harness = new WorldHarness(
                // Three demons side by side in front of the player, inside the normal attack's cone (220 cm, 60 degrees each side).
                map: TestCombat.OpenMap(TestCombat.MonsterMarker(-60, 150, id: "demon.a"), TestCombat.MonsterMarker(0, 150, id: "demon.b"),
                    TestCombat.MonsterMarker(60, 150, id: "demon.c")),
                events: new EventDispatchHook(Bus), bus: Bus,
                actions: ActionCatalog.From(tables),
                combat: TestCombat.Rules(tables, TestCombat.Point("demon.a"), TestCombat.Point("demon.b"), TestCombat.Point("demon.c")),
                progression: ProgressionRules.From(tables));
            Player = Harness.Join(1); // the join tick also spawns the demons
        }

        public WorldHarness Harness { get; }
        public EventBus Bus { get; }
        public LevelLog Levels { get; } = new();
        public Player Player { get; }
        public ProgressionStore Store => Harness.World.Progression!;

        /// <summary>Kills <paramref name="count"/> demons in front of the player with one normal attack (their HP set to 1, as HitWorldTests does).</summary>
        public void KillDemons(int count)
        {
            var demons = Harness.World.Entities.OfType<Monster>().Take(count).ToList();
            Assert.Equal(count, demons.Count);
            foreach (var demon in demons)
                demon.Combatant!.Meters.Set(ResourceKind.Hp, 1, Harness.World.NowMs);
            Harness.SendInputs(1, TestWorlds.Frame(1, facing: 0, actions: InputActions.Attack));
            Harness.Ticks(8); // 300 ms of startup, the strike, the deaths, the dispatch and the send
            Assert.All(demons, d => Assert.False(d.Combatant!.IsAlive));
        }
    }

    private sealed class LevelLog : IEventConsumer<LevelUp>
    {
        public List<LevelUp> Events { get; } = [];
        public void Handle(in EventEnvelope envelope, in LevelUp payload) => Events.Add(payload);
    }

    [Fact]
    public void A_demon_killed_by_a_real_hit_pays_its_exp_to_the_killer()
    {
        var s = new Setup();

        s.KillDemons(1);

        Assert.Equal(new ProgressSnapshot(1, DemonExp, Rank.F, 1), s.Store.Snapshot(s.Player.CharacterId));
        var state = s.Harness.Transport.ProgressionFor(1)[^1];
        Assert.Equal((1, (long)DemonExp, 172L), (state.Level, state.Exp, state.ExpToNext));
    }

    [Fact]
    public void Killing_demons_takes_a_character_to_level_5()
    {
        var s = new Setup();
        // Most of the way there in one go (the kills before), then real demon kills for the rest.
        s.Harness.World.Exp!.Grant(s.Player.CharacterId, s.Player.Id, ExpToLevel5 - 3 * DemonExp);
        Assert.Equal(4, s.Store.Snapshot(s.Player.CharacterId).Level);

        s.KillDemons(3);

        Assert.Equal(new ProgressSnapshot(5, 0, Rank.F, 5), s.Store.Snapshot(s.Player.CharacterId));
        Assert.Equal((4, 5), (s.Levels.Events[^1].FromLevel, s.Levels.Events[^1].ToLevel));
        var state = s.Harness.Transport.ProgressionFor(1)[^1];
        Assert.Equal((5, 5, ProgressionCause.LevelUp), (state.Level, state.SkillPoints, state.Cause));
        Assert.Equal(3, s.Store.KillCount(s.Player.CharacterId, TestCombat.MonsterId));
    }
}
