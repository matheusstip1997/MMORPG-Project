using GameServer.Combat;
using GameServer.Entities;
using GameServer.Events;
using GameServer.Tests.Support;
using Shared.Protocol;
using Shared.Simulation;
using Shared.Simulation.Actions;
using Shared.Simulation.Stats;

namespace GameServer.Tests.World;

/// <summary>
/// Hit resolution through the tick (backlog D3, D4): a player's normal attack fires its strike in phase 3, phase 5 tests
/// its cone against the monster (rewound to what the player saw, R-3.1-03), phase 8 removes a killed monster, phase 10
/// sends the hits to observers. Repository tables at 20 Hz: attack.basic has 300 ms (6 ticks) of Startup, so its strike
/// fires 6 ticks after the press; its cone reaches 220 cm at 60 degrees each side.
/// </summary>
public class HitWorldTests
{
    private const ushort North = 0; // facing 0 is +Y

    private sealed class HitCounter : IEventConsumer<HitLanded>, IEventConsumer<Staggered>
    {
        public int Count { get; private set; }

        public int Staggers { get; private set; }

        public void Handle(in EventEnvelope envelope, in HitLanded payload) => Count++;

        public void Handle(in EventEnvelope envelope, in Staggered payload) => Staggers++;
    }

    private sealed class Setup
    {
        public Setup(int monsterXCm = 0, int monsterYCm = 150, string monsterId = TestCombat.MonsterId, bool log = true)
        {
            var tables = TestCombat.Tables();
            Bus = new EventBus(1, new FakeClock(), TestPipelineSettings.BusLimits());
            Log = log ? CombatEventLog.Attach(Bus) : new CombatEventLog();
            Harness = new WorldHarness(
                map: TestCombat.OpenMap(TestCombat.MonsterMarker(monsterXCm, monsterYCm)),
                events: new EventDispatchHook(Bus), bus: Bus,
                actions: ActionCatalog.From(tables),
                combat: TestCombat.Rules(tables, TestCombat.Point(monsterId: monsterId)));
            Player = Harness.Join(1); // the join tick also spawns the monster
            Monster = Assert.Single(Harness.World.Entities.OfType<Monster>());
        }

        public WorldHarness Harness { get; }

        public EventBus Bus { get; }

        public CombatEventLog Log { get; }

        public Player Player { get; }

        public Monster Monster { get; }

        public uint Tick => Harness.World.ServerTick;

        public long NowMs => Harness.World.NowMs;

        /// <summary>Presses attack in the frame the next tick applies; <paramref name="viewLag"/> ticks behind, if any.</summary>
        public void Attack(ushort sequence, ushort facing = North, uint? viewLag = null)
        {
            var frame = TestWorlds.Frame(sequence, facing: facing, actions: InputActions.Attack);
            if (viewLag is { } lag)
                frame.ViewTick = Tick - lag;
            Harness.SendInputs(1, frame);
        }

        public void MoveMonster(int xCm, int yCm)
        {
            Monster.Movement = new MovementState(FixVec2.FromCm(xCm, yCm), Monster.Movement.Facing);
            Harness.World.Grid.Update(Monster);
        }
    }

    [Fact]
    public void A_normal_attack_hits_the_monster_in_front_when_its_strike_fires()
    {
        var s = new Setup(monsterYCm: 150);
        int hp = s.Monster.Combatant!.Hp(s.NowMs);
        uint pressTick = s.Tick;
        s.Attack(1);

        s.Harness.Ticks(6); // Startup
        Assert.Equal(hp, s.Monster.Combatant.Hp(s.NowMs));
        s.Harness.Ticks(1); // Active: the strike fires in phase 3 and lands in phase 5

        Assert.True(s.Monster.Combatant.Hp(s.NowMs) < hp);
        Assert.Equal(ActionState.Flinched, s.Monster.Actions.State);
        var (tick, hit) = Assert.Single(s.Harness.Transport.HitsFor(1));
        Assert.Equal(pressTick + 6, tick);
        Assert.Equal((s.Player.Id, s.Monster.Id, HitKind.Damaged), (hit.AttackerId, hit.TargetId, hit.Kind));
        Assert.Equal(hp - hit.Amount, s.Monster.Combatant.Hp(s.NowMs));
        Assert.Equal([typeof(HitLanded), typeof(Damaged), typeof(Flinched)], s.Log.Kinds);
        Assert.Equal(s.Harness.Clock.UtcNowMs, s.Player.LastCombatAtMs);

        // The flinch (300 ms) runs out in phase 3 although monsters have no AI yet.
        s.Harness.Ticks(6);
        Assert.Equal(ActionState.Idle, s.Monster.Actions.State);
    }

    [Theory]
    [InlineData(0, -150)] // behind
    [InlineData(0, 300)] // beyond 220 cm plus its 50 cm body
    [InlineData(200, 50)] // outside the 60 degree half angle
    public void A_monster_outside_the_cone_is_not_hit(int xCm, int yCm)
    {
        var s = new Setup(monsterXCm: xCm, monsterYCm: yCm);
        int hp = s.Monster.Combatant!.Hp(s.NowMs);
        s.Attack(1);
        s.Harness.Ticks(10);

        Assert.Equal(hp, s.Monster.Combatant.Hp(s.NowMs));
        Assert.Empty(s.Harness.Transport.HitsFor(1));
        Assert.Empty(s.Log.Events);
    }

    [Fact]
    public void A_player_hits_where_it_saw_the_monster_rewound_by_its_view_lag()
    {
        // R-3.1-03: the monster stood in front until 3 ticks before the strike, then left the cone.
        var s = new Setup(monsterYCm: 150);
        s.Attack(1, viewLag: 4);
        s.Harness.Ticks(3);
        s.MoveMonster(0, 1_000);
        s.Harness.Ticks(4);

        Assert.Equal(4, s.Player.ViewLagTicks);
        Assert.Single(s.Harness.Transport.HitsFor(1));
    }

    [Fact]
    public void Without_a_view_tick_the_monster_is_tested_where_it_stands()
    {
        var s = new Setup(monsterYCm: 150);
        s.Attack(1);
        s.Harness.Ticks(3);
        s.MoveMonster(0, 1_000);
        s.Harness.Ticks(4);

        Assert.Equal(0, s.Player.ViewLagTicks);
        Assert.Empty(s.Harness.Transport.HitsFor(1));
    }

    [Fact]
    public void The_rewind_is_capped_at_200_ms_whatever_the_client_claims()
    {
        // The monster left 5 ticks before the strike: a claimed lag of 50 ticks is cut to the cap (4 ticks) and misses.
        var s = new Setup(monsterYCm: 150);
        s.Attack(1, viewLag: 50);
        s.Harness.Ticks(2);
        s.MoveMonster(0, 1_000);
        s.Harness.Ticks(5);

        Assert.Equal(s.Harness.World.Combat!.LagCompensationCapTicks, s.Player.ViewLagTicks);
        Assert.Equal(4, s.Player.ViewLagTicks);
        Assert.Empty(s.Harness.Transport.HitsFor(1));
    }

    [Fact]
    public void A_view_tick_from_the_future_counts_as_no_lag()
    {
        var s = new Setup();
        var frame = TestWorlds.Frame(1);
        frame.ViewTick = s.Tick + 5;
        s.Harness.SendInputs(1, frame);
        s.Harness.Ticks(1);
        Assert.Equal(0, s.Player.ViewLagTicks);
    }

    [Fact]
    public void A_killed_monster_leaves_with_its_killing_blow_sent_and_respawns_after_30_s()
    {
        var s = new Setup(monsterYCm: 150);
        var monster = s.Monster;
        monster.Combatant!.Meters.Set(ResourceKind.Hp, 1, s.NowMs);
        s.Attack(1);
        s.Harness.Ticks(7);
        uint deathTick = s.Tick - 1;

        Assert.False(monster.Combatant.IsAlive);
        Assert.Equal(deathTick, monster.Combatant.DiedAtTick);
        Assert.DoesNotContain(monster, s.Harness.World.Entities); // phase 8 of the same tick
        var (_, hit) = Assert.Single(s.Harness.Transport.HitsFor(1));
        Assert.Equal((monster.Id, HitKind.Killed, 1), (hit.TargetId, hit.Kind, hit.Amount));
        Assert.Contains(monster.Id, s.Harness.LastSnapshot(1).Despawns);
        Assert.Equal([typeof(HitLanded), typeof(Damaged), typeof(Killed), typeof(Died)], s.Log.Kinds);

        int respawnTicks = s.Harness.World.Combat!.MonsterRespawnTicks;
        Assert.Equal(600, respawnTicks);
        s.Harness.Ticks(respawnTicks - 1);
        Assert.Empty(s.Harness.World.Entities.OfType<Monster>());
        s.Harness.Ticks(1);
        var respawned = Assert.Single(s.Harness.World.Entities.OfType<Monster>());
        Assert.NotEqual(monster.Id, respawned.Id);
        Assert.Equal(respawned.Combatant!.MaxHp(s.NowMs), respawned.Combatant.Hp(s.NowMs));
    }

    [Fact]
    public void Steady_state_combat_ticks_allocate_nothing()
    {
        // Three warriors take turns attacking the boss (5000 HP), so strikes, hits, events and hit messages run every few ticks.
        var s = new Setup(monsterYCm: 150, monsterId: TestCombat.BossId, log: false); // the log boxes events
        var landed = new HitCounter();
        s.Bus.Subscribe<HitLanded>(landed);
        s.Bus.Subscribe<Staggered>(landed);
        s.Harness.Join(2);
        s.Harness.Join(3);
        s.Harness.Transport.Record = false;
        var packet = new byte[InputPacket.MaxSize];
        var frames = new InputFrame[1];
        ushort sequence = 0;

        void Run(int ticks)
        {
            for (int i = 0; i < ticks; i++)
            {
                sequence++;
                // Press every 15 ticks (the attack takes 14), release in between; stamina refusals keep it varied.
                var actions = sequence % 15 == 0 ? InputActions.Attack : InputActions.None;
                frames[0] = TestWorlds.Frame(sequence, facing: North, actions: actions);
                frames[0].ViewTick = s.Tick > 2 ? s.Tick - 2 : 0;
                int length = InputPacket.Write(packet, frames);
                for (int peer = 1; peer <= 3; peer++)
                    s.Harness.Router.OnDataReceived(peer, Channel.UnreliableSequenced, packet.AsSpan(0, length));
                s.Harness.World.Tick();
            }
        }

        // Warm-up until the boss has been staggered once: the bus creates each event type's queue on its first publish.
        while (landed.Staggers == 0 && sequence < 2_000)
            Run(1);
        Run(50);
        Assert.True(landed.Staggers > 0);
        int hitsBefore = landed.Count;
        long before = GC.GetAllocatedBytesForCurrentThread();
        Run(300);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(s.Monster.Combatant!.IsAlive);
        Assert.True(landed.Count > hitsBefore + 10, "the boss was hit while measuring");
        Assert.Equal(0, allocated);
    }
}
