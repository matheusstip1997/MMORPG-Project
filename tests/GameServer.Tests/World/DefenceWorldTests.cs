using GameServer.Combat;
using GameServer.Entities;
using GameServer.Events;
using GameServer.Tests.Support;
using GameServer.World;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Actions;

namespace GameServer.Tests.World;

/// <summary>
/// Defence through the tick (R-3.1-03, R-3.1-05, R-3.3-02 to R-3.3-04): the demon's AI (backlog F) aggroes the player
/// 1.5 m south of it and attacks; its strike goes through the pending-hit path. The player's frames carry a view tick 2
/// ticks behind; phase 5 lands the hit once that lag has passed, judged on what the player saw; phase 10 sends the hit,
/// the parry result and the telegraph color. The demon's attacks get a test color here (the M1 data is uncolored).
/// </summary>
public class DefenceWorldTests
{
    private const int Lag = 2;

    private sealed class Setup
    {
        private ushort _sequence;

        public Setup(ParryColor color)
        {
            var tables = TestCombat.Tables();
            var catalog = ActionCatalog.From(tables);
            foreach (var id in new[] { "monster.demon.claw", "monster.demon.pounce" })
                catalog.Find(id)!.Def.ParryClassPve = color;
            PlayerAttack = catalog.Find("attack.warrior")!;
            Bus = new EventBus(1, new FakeClock(), TestPipelineSettings.BusLimits());
            Log = CombatEventLog.Attach(Bus);
            Bus.Subscribe(Starts);
            Harness = new WorldHarness(
                map: TestCombat.OpenMap(TestCombat.MonsterMarker(0, 150)),
                events: new EventDispatchHook(Bus), bus: Bus, actions: catalog,
                combat: TestCombat.Rules(tables, TestCombat.Point()));
            Player = Harness.Join(1);
            Monster = Assert.Single(Harness.World.Entities.OfType<Monster>());
            Harness.Router.OnRoundTripTime(1, 20); // 20 + 100 + 50 ms allows the 2-tick lag
            // After phase 3: note the tick the demon's strike fires (the AI's monster step routes it itself).
            Harness.World.PhaseStarted = phase =>
            {
                if (phase != TickPhase.ProjectileMovement || StrikeTick != 0 || Monster.Actions.Action is not { } action)
                    return;
                foreach (var effect in action.Effects)
                {
                    if (effect.Kind == EffectKind.Strike && effect.FireTick == Monster.Actions.Elapsed)
                        StrikeTick = Tick;
                }
            };
        }

        public WorldHarness Harness { get; }
        public EventBus Bus { get; }
        public CombatEventLog Log { get; }
        public StartLog Starts { get; } = new();
        public Player Player { get; }
        public Monster Monster { get; }
        public ActionTiming PlayerAttack { get; }
        public uint StrikeTick { get; private set; }
        public uint Tick => Harness.World.ServerTick;

        public IEnumerable<(uint Tick, HitEntry Hit)> MonsterHits =>
            Harness.Transport.HitsFor(1).Where(h => h.Hit.AttackerId == Monster.Id);

        /// <summary>One tick with a frame that saw <see cref="Lag"/> ticks back.</summary>
        public void Step(InputActions held = InputActions.None)
        {
            var frame = TestWorlds.Frame(++_sequence, actions: held);
            frame.ViewTick = Tick - Lag;
            Harness.SendInputs(1, frame);
            Harness.World.Tick();
        }

        public void StepUntilStrike()
        {
            for (int i = 0; i < 200 && StrikeTick == 0; i++)
                Step();
            Assert.NotEqual(0u, StrikeTick);
        }

        public void StepUntilMonsterAttacks()
        {
            for (int i = 0; i < 200 && Monster.Actions.State != ActionState.Startup; i++)
                Step();
            Assert.Equal(ActionState.Startup, Monster.Actions.State);
        }
    }

    [Fact]
    public void A_monster_strike_lands_once_the_defenders_view_lag_has_passed()
    {
        var s = new Setup(ParryColor.None);
        s.StepUntilStrike();
        Assert.Empty(s.MonsterHits);
        Assert.Equal(1, s.Harness.World.Hits!.Pending.Count);
        s.Step();
        Assert.Empty(s.MonsterHits);
        s.Step();

        var (tick, hit) = Assert.Single(s.MonsterHits);
        Assert.Equal(s.StrikeTick + Lag, tick);
        Assert.Equal((s.Monster.Id, s.Player.Id, HitKind.Damaged), (hit.AttackerId, hit.TargetId, hit.Kind));
    }

    [Fact]
    public void A_parry_pressed_after_the_server_hit_but_in_time_on_the_defenders_screen_succeeds()
    {
        var s = new Setup(ParryColor.Green);
        s.StepUntilStrike();
        int hp = s.Player.Combatant!.Hp(s.Harness.World.NowMs);
        // Applied one tick after the server hit, this frame saw StrikeTick - 1: the hit falls on the parry's first window tick.
        s.Step(InputActions.Parry);
        s.Step(InputActions.Parry);

        var (_, hit) = Assert.Single(s.MonsterHits);
        Assert.Equal(HitKind.Parried, hit.Kind);
        Assert.Equal(hp, s.Player.Combatant.Hp(s.Harness.World.NowMs));
        var result = Assert.Single(s.Harness.Transport.ActionResultsFor(1));
        Assert.Equal((ushort)3, result.ActionId);
        Assert.Equal(ActionReason.ParrySuccess, result.Reason);
        var parry = Assert.Single(s.Log.Events.OfType<ParryAttempted>());
        Assert.Equal((s.Player.Id, s.Monster.Id, ParryColor.Green, ParryResult.Success),
            (parry.DefenderId, parry.AttackerId, parry.Color, parry.Result));
    }

    [Fact]
    public void A_monster_attack_telegraphs_its_pve_color_in_the_start_event_and_snapshots()
    {
        var s = new Setup(ParryColor.Yellow);
        s.Step();
        Assert.Equal(ParryColor.None, Monster(s).ParryColor);
        s.StepUntilMonsterAttacks();
        s.Bus.DispatchQueued();

        var monster = Monster(s);
        Assert.True(monster.ActionId >= 201);
        Assert.Equal(ParryColor.Yellow, monster.ParryColor);
        var started = Assert.Single(s.Starts.Events, e => e.EntityId == s.Monster.Id);
        Assert.Equal((monster.ActionId, ParryColor.Yellow), ((ushort)started.ActionId, started.Color));
        Assert.Equal(ParryColor.None, s.Harness.LastSnapshot(1).Self.ParryColor);
    }

    [Fact]
    public void A_players_action_start_publishes_its_pvp_color()
    {
        // R-3.1-05: ActionStarted carries the color; a player's attack uses parryClassPvp (R-3.3-06).
        var s = new Setup(ParryColor.None);
        s.PlayerAttack.Def.ParryClassPve = ParryColor.Red;
        s.PlayerAttack.Def.ParryClassPvp = ParryColor.Blue;
        s.Step(InputActions.Attack);
        s.Bus.DispatchQueued();

        var started = Assert.Single(s.Starts.Events, e => e.EntityId == s.Player.Id);
        Assert.Equal((s.PlayerAttack.NetId, ParryColor.Blue), (started.ActionId, started.Color));
        Assert.Equal(ParryColor.Blue, s.Harness.LastSnapshot(1).Self.ParryColor);
    }

    private sealed class StartLog : IEventConsumer<ActionStarted>
    {
        public List<ActionStarted> Events { get; } = [];

        public void Handle(in EventEnvelope envelope, in ActionStarted payload) => Events.Add(payload);
    }

    private static EntitySnapshot Monster(Setup s) =>
        s.Harness.LastSnapshot(1).Entities.Single(e => e.EntityId == s.Monster.Id);
}
