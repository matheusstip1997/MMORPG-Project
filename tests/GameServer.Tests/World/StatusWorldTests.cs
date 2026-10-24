using GameServer.Combat;
using GameServer.Entities;
using GameServer.Events;
using GameServer.Tests.Combat;
using GameServer.Tests.Support;
using Shared.Protocol;
using Shared.Simulation.Actions;
using Shared.Simulation.Stats;

namespace GameServer.Tests.World;

/// <summary>
/// Statuses in the world tick by tick (backlog D6, spec 3.7): a player casts the placeholder Stamina buff from its hotbar
/// (phase 3 fires it, phase 6 applies it), its own snapshot header carries it for the buff row (protocol 7) with the ticks
/// left, and it expires on the tick its duration ends.
/// </summary>
public class StatusWorldTests
{
    private const byte HasteSlot = 3; // ActionBindings: skill.crossCut, skill.heal, skill.haste, ...

    private sealed class Setup
    {
        public Setup()
        {
            var tables = TestCombat.Tables();
            Bus = new EventBus(1, new FakeClock(), TestPipelineSettings.BusLimits());
            Log = StatusEventLog.Attach(Bus);
            // The monster spawns far away (out of interest and aggro range).
            Harness = new WorldHarness(
                map: TestCombat.OpenMap(TestCombat.MonsterMarker(8_000, 0)),
                events: new EventDispatchHook(Bus), bus: Bus,
                actions: ActionCatalog.From(tables),
                combat: TestCombat.Rules(tables, TestCombat.Point()));
            Player = Harness.Join(1);
            Assert.True(Harness.World.Combat!.Statuses.TryGet("staminaBuff", out var buff));
            Buff = buff;
        }

        public WorldHarness Harness { get; }

        public EventBus Bus { get; }

        public StatusEventLog Log { get; }

        public Player Player { get; }

        public StatusDefinition Buff { get; }

        public uint Tick => Harness.World.ServerTick;
    }

    [Fact]
    public void A_player_casts_a_buff_sees_it_in_its_snapshot_and_it_expires_on_time()
    {
        var s = new Setup();
        var combatant = s.Player.Combatant!;
        var ids = s.Harness.World.Combat!.Config.Ids;
        int baseRegen = combatant.Stats.Get(ids.StaminaRegen);
        Assert.Empty(s.Harness.LastSnapshot(1).OwnStatuses);

        s.Harness.SendInputs(1, TestWorlds.Frame(1, skillSlot: HasteSlot));
        int waited = 0;
        while (!combatant.Statuses.Has("staminaBuff"))
        {
            s.Harness.Ticks(1);
            Assert.True(++waited < 20, "the buff never fired");
        }
        uint applied = s.Tick - 1;

        // Phase 6 applied it; phase 10 of the same tick already shows it with its whole duration left.
        var status = Assert.Single(s.Harness.LastSnapshot(1).OwnStatuses);
        Assert.Equal(new OwnStatus { NetId = (ushort)s.Buff.NetId, Stacks = 1, RemainingTicks = (ushort)s.Buff.DurationTicks }, status);
        Assert.Equal(baseRegen * 150 / 100, combatant.Stats.Get(ids.StaminaRegen));

        // One tick before its end it is still there with 1 tick left.
        s.Harness.Ticks(s.Buff.DurationTicks - 1);
        Assert.Equal(applied + (uint)s.Buff.DurationTicks - 1, s.Harness.LastSnapshot(1).Header.ServerTick);
        Assert.Equal((ushort)1, Assert.Single(s.Harness.LastSnapshot(1).OwnStatuses).RemainingTicks);

        // Phase 6 of its end tick removes it.
        s.Harness.Ticks(1);
        Assert.Empty(s.Harness.LastSnapshot(1).OwnStatuses);
        Assert.False(combatant.Statuses.Has("staminaBuff"));
        Assert.Equal(baseRegen, combatant.Stats.Get(ids.StaminaRegen));
        Assert.Equal(
            [new EffectApplied(s.Player.Id, s.Player.Id, s.Buff.NetId, 1, applied + s.Buff.DurationTicks, false),
             new EffectRemoved(s.Player.Id, s.Buff.NetId, StatusEndReason.Expired)],
            s.Log.Events);
    }

    [Fact]
    public void A_player_who_dies_loses_its_statuses_and_respawns_without_them()
    {
        var s = new Setup();
        var combatant = s.Player.Combatant!;
        s.Harness.World.Statuses!.Apply(s.Player, s.Player, s.Buff, s.Tick);
        s.Harness.Ticks(1);
        Assert.Single(s.Harness.LastSnapshot(1).OwnStatuses);

        combatant.Meters.Drain(ResourceKind.Hp, combatant.Hp(s.Harness.World.NowMs), s.Harness.World.NowMs);
        combatant.Die(s.Tick);
        ActionMachine.Kill(s.Player.Actions);
        s.Harness.Ticks(1);

        Assert.Empty(s.Harness.LastSnapshot(1).OwnStatuses);
        Assert.Contains(new EffectRemoved(s.Player.Id, s.Buff.NetId, StatusEndReason.Died), s.Log.Events);
    }
}
