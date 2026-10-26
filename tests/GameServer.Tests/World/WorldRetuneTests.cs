using GameServer.AI;
using GameServer.Combat;
using GameServer.Entities;
using GameServer.Tests.Support;
using GameServer.World;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;
using Shared.Simulation.Actions;

namespace GameServer.Tests.World;

/// <summary>
/// The world's side of the dev hot reload (spec 11.7): a posted plan swaps in at the start of the next tick, never in the
/// middle of one; running actions end, the next action runs the new timing, statuses move to their new rows, projectiles
/// in flight keep theirs, monsters drop their requested attack. The committed tables, a warrior at the origin facing north.
/// </summary>
public class WorldRetuneTests
{
    private const ushort North = 0;

    private sealed class Setup
    {
        public Setup(string classId = "warrior", int monsterYCm = 3_000)
        {
            var tables = TestCombat.Tables();
            Markers = [TestCombat.MonsterMarker(0, monsterYCm)];
            Harness = new WorldHarness(
                map: TestCombat.OpenMap(Markers),
                actions: ActionCatalog.From(tables),
                combat: TestCombat.Rules(tables, TestCombat.Point()),
                projectiles: ProjectileRules.From(tables));
            Harness.Characters.AutoCreateClassId = classId;
            Player = Harness.Join(1);
        }

        public GameServer.World.Map.WorldMarker[] Markers { get; }
        public WorldHarness Harness { get; }
        public Player Player { get; }
        public GameWorld World => Harness.World;

        /// <summary>A plan from freshly loaded committed tables after <paramref name="tune"/> changed their rows.</summary>
        public RetunePlan Plan(Action<ConfigSet> tune)
        {
            var tables = TestCombat.Tables();
            tune(tables);
            var actions = ActionCatalog.From(tables);
            var combat = TestCombat.Rules(tables, TestCombat.Point());
            var projectiles = ProjectileRules.From(tables);
            var ai = new MonsterAiRules(combat.MonsterAi!, combat, actions, World.Map, projectiles);
            return new RetunePlan(actions, combat, projectiles, ai, hash: 42, ["test"]);
        }

        public void Press(ushort sequence, InputActions actions = InputActions.Attack, byte slot = 0) =>
            Harness.SendInputs(1, TestWorlds.Frame(sequence, facing: North, actions: actions, skillSlot: slot));
    }

    private static T Row<T>(ConfigSet set, Func<T, bool> match)
    {
        Assert.True(set.TryGet(typeof(T), out var table));
        return table.Rows.Cast<T>().Single(match);
    }

    [Fact]
    public async Task A_posted_plan_swaps_in_at_the_start_of_the_next_tick()
    {
        var s = new Setup();
        var plan = s.Plan(_ => { });
        s.World.Retunes.Post(plan);
        Assert.NotSame(plan.Actions, s.World.Actions); // nothing happens until a tick runs

        uint tick = s.World.ServerTick;
        s.World.Tick();

        Assert.True(plan.Applied.IsCompletedSuccessfully);
        Assert.Equal(tick, await plan.Applied);
        Assert.Same(plan.Actions, s.World.Actions);
        Assert.Same(plan.Combat, s.World.Combat);
        Assert.Same(plan.Actions, s.Player.Actions.Catalog);
        Assert.Null(s.World.Retunes.Take());
    }

    [Fact]
    public void A_running_action_ends_and_the_next_one_runs_the_new_timing()
    {
        var s = new Setup();
        s.Press(1);
        s.Harness.Ticks(2);
        Assert.Equal(ActionState.Startup, s.Player.Actions.State);
        var oldTicks = s.Player.Actions.Action!.TotalTicks;

        s.World.Retunes.Post(s.Plan(t => Row<ActionDef>(t, a => a.Id == "attack.warrior").RecoveryMs += 500));
        s.World.Tick();

        Assert.Equal(ActionState.Idle, s.Player.Actions.State);
        s.Press(2, InputActions.None);
        s.World.Tick();
        s.Press(3);
        s.World.Tick();
        Assert.Equal("attack.warrior", s.Player.Actions.Action?.Id);
        Assert.Equal(oldTicks + 10, s.Player.Actions.Action!.TotalTicks); // 500 ms more at 20 Hz
    }

    [Fact]
    public void An_active_status_moves_to_its_new_row_and_keeps_its_time()
    {
        var s = new Setup();
        var rules = s.World.Combat!;
        Assert.True(rules.Statuses.TryGet("magicBarrier", out var barrier));
        uint tick = s.World.ServerTick;
        Assert.True(s.World.Statuses!.Apply(s.Player, s.Player, barrier, tick));
        int flinchResist = rules.Config.Ids.FlinchResist;
        int before = s.Player.Combatant!.Stats.Get(flinchResist);
        uint endTick = s.Player.Combatant.Statuses[0].EndTick;

        s.World.Retunes.Post(s.Plan(t => Row<StatusModifierDef>(t, m => m.Id == "magicBarrier.flinchResist").Flat += 30));
        s.World.Tick();

        Assert.Equal(before + 30, s.Player.Combatant.Stats.Get(flinchResist));
        Assert.Equal(endTick, s.Player.Combatant.Statuses[0].EndTick);
        Assert.True(s.World.Combat!.Statuses.TryGet("magicBarrier", out var reloaded));
        Assert.Same(reloaded, s.Player.Combatant.Statuses[0].Definition);
    }

    [Fact]
    public void A_projectile_in_flight_keeps_its_row_and_the_next_one_uses_the_new_one()
    {
        var s = new Setup("archer");
        var projectiles = s.World.Projectiles!;
        s.Press(1);
        for (int i = 0; i < 40 && projectiles.Active.Count == 0; i++)
            s.World.Tick();
        var flying = Assert.Single(projectiles.Active);
        var oldDef = flying.Def;

        s.World.Retunes.Post(s.Plan(t => Row<ProjectileDef>(t, p => p.Id == oldDef.Id).SpeedCmPerSec -= 100));
        s.World.Tick();

        Assert.Same(oldDef, flying.Def);
        Assert.True(projectiles.Rules.TryGet(oldDef.Id, out var reloaded));
        Assert.NotSame(oldDef, reloaded);
        Assert.Equal(oldDef.SpeedCmPerSec - 100, reloaded.SpeedCmPerSec);
    }

    [Fact]
    public void A_monster_moves_to_its_new_row_and_drops_its_requested_attack()
    {
        var s = new Setup();
        var monster = s.World.Entities.OfType<Monster>().Single();
        var oldDef = monster.Def;
        monster.Brain.RequestedAction = 0;
        var plan = s.Plan(t => Row<MonsterDef>(t, m => m.Id == oldDef.Id).LeashRangeCm += 100);

        s.World.ApplyRetune(plan, s.World.ServerTick);

        Assert.NotSame(oldDef, monster.Def);
        Assert.Equal(oldDef.LeashRangeCm + 100, monster.Def.LeashRangeCm);
        Assert.Equal(-1, monster.Brain.RequestedAction);
        Assert.Same(plan.Actions, monster.Actions.Catalog);
    }
}
