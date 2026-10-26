using GameServer.Combat;
using GameServer.Entities;
using GameServer.Tests.Support;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;
using Shared.Simulation.Actions;

namespace GameServer.Tests.Combat;

/// <summary>
/// The strike trace of the dev hitbox gizmos (spec 11.7): <see cref="StrikeResolver"/> reports each strike with the exact
/// origin and locked facing it tests, and each candidate target with the position and radius it tests it at.
/// </summary>
public class StrikeTraceTests
{
    private sealed class RecordingTrace : IStrikeTraceSink
    {
        public List<(uint Tick, uint Attacker, int ActionNetId, int EffectIndex, FixVec2 Origin, ushort Facing)> Strikes { get; } = [];
        public List<(uint Tick, uint Attacker, uint Target, FixVec2 Position, int RadiusRaw)> Targets { get; } = [];

        public void StrikePlaced(uint tick, uint attackerId, int actionNetId, int effectIndex, FixVec2 origin, ushort facing) =>
            Strikes.Add((tick, attackerId, actionNetId, effectIndex, origin, facing));

        public void TargetTested(uint tick, uint attackerId, uint targetId, FixVec2 position, int radiusRaw) =>
            Targets.Add((tick, attackerId, targetId, position, radiusRaw));
    }

    [Fact]
    public void A_strike_and_the_target_it_tested_are_reported_as_the_server_placed_them()
    {
        var tables = TestCombat.Tables();
        Assert.True(tables.TryGet(typeof(MonsterDef), out var monsters));
        monsters.Rows.Cast<MonsterDef>().Single(m => m.Id == TestCombat.MonsterId).AggroRangeCm = 1; // waits for the hit
        var trace = new RecordingTrace();
        var harness = new WorldHarness(
            map: TestCombat.OpenMap(TestCombat.MonsterMarker(0, 150)),
            actions: ActionCatalog.From(tables),
            combat: TestCombat.Rules(tables, TestCombat.Point()),
            strikeTrace: trace);
        harness.Characters.AutoCreateClassId = "warrior";
        var player = harness.Join(1);
        var monster = harness.World.Entities.OfType<Monster>().Single();
        var attack = harness.World.Actions.Find("attack.warrior")!;
        int strikeIndex = Array.FindIndex(attack.Effects, e => e.Kind == EffectKind.Strike);

        harness.SendInputs(1, TestWorlds.Frame(1, facing: 0, actions: InputActions.Attack));
        harness.Ticks(attack.TotalTicks + 1);

        var strike = Assert.Single(trace.Strikes);
        Assert.Equal(player.Id, strike.Attacker);
        Assert.Equal(attack.NetId, strike.ActionNetId);
        Assert.Equal(strikeIndex, strike.EffectIndex);
        Assert.Equal(player.Movement.Position, strike.Origin);
        Assert.Equal(player.Actions.Facing, strike.Facing);
        var target = Assert.Single(trace.Targets, t => t.Target == monster.Id);
        Assert.Equal(strike.Tick, target.Tick);
        Assert.Equal(monster.Movement.Position, target.Position); // no view lag claimed: not rewound
        Assert.Equal(monster.Combatant!.BodyRadiusRaw, target.RadiusRaw);
    }
}
