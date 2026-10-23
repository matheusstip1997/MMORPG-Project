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
/// Defence through the tick (R-3.1-03, R-3.1-05, R-3.3-02 to R-3.3-04): a monster's basic attack on the player, the
/// monster's machine driven directly by the test (no AI yet) and its strike handed to the world's effect router when it
/// fires. The player's frames carry a view tick 2 ticks behind; phase 5 lands the hit once that lag has passed, judged
/// on what the player saw; phase 10 sends the hit, the parry result and the telegraph color.
/// </summary>
public class DefenceWorldTests
{
    private const ushort South = 32_768; // the monster stands north of the player and faces it
    private const int Lag = 2;

    private sealed class Setup
    {
        private ushort _sequence;

        public Setup(ParryColor color)
        {
            var tables = TestCombat.Tables();
            var catalog = ActionCatalog.From(tables);
            Attack = catalog.Find("attack.basic")!;
            Attack.Def.ParryClassPve = color;
            Bus = new EventBus(1, new FakeClock(), TestPipelineSettings.BusLimits());
            Log = CombatEventLog.Attach(Bus);
            Harness = new WorldHarness(
                map: TestCombat.OpenMap(TestCombat.MonsterMarker(0, 150)),
                events: new EventDispatchHook(Bus), bus: Bus, actions: catalog,
                combat: TestCombat.Rules(tables, TestCombat.Point()));
            Player = Harness.Join(1);
            Monster = Assert.Single(Harness.World.Entities.OfType<Monster>());
            Harness.Router.OnRoundTripTime(1, 20); // 20 + 100 + 50 ms allows the 2-tick lag
            // Hand the monster's strike to phase 5 when its machine fires it (the AI's job later, backlog F).
            Harness.World.PhaseStarted = phase =>
            {
                if (phase == TickPhase.ProjectileMovement && Monster.Actions.ActionIndex == Attack.Index
                    && Monster.Actions.Elapsed == Attack.Effects[0].FireTick)
                {
                    StrikeTick = Tick;
                    Harness.World.EffectRouter.OnEffect(Monster, new FiredEffect(Attack, 0), Tick);
                }
            };
        }

        public WorldHarness Harness { get; }
        public EventBus Bus { get; }
        public CombatEventLog Log { get; }
        public Player Player { get; }
        public Monster Monster { get; }
        public ActionTiming Attack { get; }
        public uint StrikeTick { get; private set; }
        public uint Tick => Harness.World.ServerTick;

        public void StartMonsterAttack()
        {
            var fired = new FiredEffect[ActionMachine.MaxFiredPerStep];
            var result = ActionMachine.Step(Monster.Actions, new ActionInput(true, InputActions.Attack, 0, South, 0),
                UnlimitedActionResources.Instance, fired);
            Assert.Equal(Attack.Index, result.StartedIndex);
        }

        /// <summary>One tick with a frame that saw <see cref="Lag"/> ticks back.</summary>
        public void Step(InputActions held = InputActions.None)
        {
            var frame = TestWorlds.Frame(++_sequence, actions: held);
            frame.ViewTick = Tick - Lag;
            Harness.SendInputs(1, frame);
            Harness.World.Tick();
        }

        public void StepUntilStrike(InputActions held = InputActions.None)
        {
            while (StrikeTick == 0)
                Step(held);
        }
    }

    [Fact]
    public void A_monster_strike_lands_once_the_defenders_view_lag_has_passed()
    {
        var s = new Setup(ParryColor.None);
        s.StartMonsterAttack();
        s.StepUntilStrike();
        Assert.Empty(s.Harness.Transport.HitsFor(1));
        s.Step();
        Assert.Empty(s.Harness.Transport.HitsFor(1));
        s.Step();

        var (tick, hit) = Assert.Single(s.Harness.Transport.HitsFor(1));
        Assert.Equal(s.StrikeTick + Lag, tick);
        Assert.Equal((s.Monster.Id, s.Player.Id, HitKind.Damaged), (hit.AttackerId, hit.TargetId, hit.Kind));
    }

    [Fact]
    public void A_parry_pressed_after_the_server_hit_but_in_time_on_the_defenders_screen_succeeds()
    {
        var s = new Setup(ParryColor.Green);
        s.StartMonsterAttack();
        s.StepUntilStrike();
        int hp = s.Player.Combatant!.Hp(s.Harness.World.NowMs);
        // Applied one tick after the server hit, this frame saw StrikeTick - 1: the hit falls on the parry's first window tick.
        s.Step(InputActions.Parry);
        s.Step(InputActions.Parry);

        var (_, hit) = Assert.Single(s.Harness.Transport.HitsFor(1));
        Assert.Equal(HitKind.Parried, hit.Kind);
        Assert.Equal(hp, s.Player.Combatant.Hp(s.Harness.World.NowMs));
        var result = Assert.Single(s.Harness.Transport.ActionResultsFor(1));
        Assert.Equal((ushort)3, result.ActionId);
        Assert.Equal(ActionReason.ParrySuccess, result.Reason);
        Assert.Equal(new ParryAttempted(s.Player.Id, s.Monster.Id, 1, ParryColor.Green, ParryResult.Success),
            Assert.Single(s.Log.Events.OfType<ParryAttempted>()));
    }

    [Fact]
    public void Snapshots_carry_the_telegraph_color_of_the_running_attack()
    {
        var s = new Setup(ParryColor.Yellow);
        s.Step();
        Assert.Equal(ParryColor.None, Monster(s).ParryColor);
        s.StartMonsterAttack();
        s.Step();

        var monster = Monster(s);
        Assert.Equal((ushort)s.Attack.NetId, monster.ActionId);
        Assert.Equal(ParryColor.Yellow, monster.ParryColor);
        Assert.Equal(ParryColor.None, s.Harness.LastSnapshot(1).Self.ParryColor);
    }

    [Fact]
    public void A_players_action_start_publishes_its_pvp_color()
    {
        // R-3.1-05: ActionStarted carries the color; a player's attack uses parryClassPvp (R-3.3-06).
        var s = new Setup(ParryColor.Red);
        s.Attack.Def.ParryClassPvp = ParryColor.Blue;
        var starts = new StartLog();
        s.Bus.Subscribe(starts);
        s.Step(InputActions.Attack);
        s.Bus.DispatchQueued();

        var started = Assert.Single(starts.Events);
        Assert.Equal((s.Player.Id, s.Attack.NetId, ParryColor.Blue), (started.EntityId, started.ActionId, started.Color));
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
