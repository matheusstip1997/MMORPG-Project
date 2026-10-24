using GameServer.Entities;
using GameServer.Events;
using GameServer.Tests.Support;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;
using Shared.Simulation.Actions;
using Shared.Simulation.Stats;

namespace GameServer.Tests.World;

/// <summary>
/// The Warrior and Archer starting kits (backlog E, docs/design/skills-m1-proposal.md) through the whole tick with the
/// committed tables: each class plays its own ActionBindings row, each damaging kit action hits the demon through the
/// shared hit pipeline (strikes in phase 5, arrows in phase 4), the dashes move the caster (Back Step backward), and a
/// skill without enough MP is refused with NoMp (R-3.1-04). The player stands on the spawn marker at the origin facing
/// north (facing 0 is +Y); the demon is in front of it. Its aggro range is cut to 1 cm so it waits for the first hit.
/// </summary>
public class KitWorldTests
{
    private const ushort North = 0;

    private sealed class Setup
    {
        public Setup(string classId, int monsterYCm)
        {
            Tables = TestCombat.Tables();
            Assert.True(Tables.TryGet(typeof(MonsterDef), out var monsters));
            monsters.Rows.Cast<MonsterDef>().Single(m => m.Id == TestCombat.MonsterId).AggroRangeCm = 1;
            Bus = new EventBus(1, new FakeClock(), TestPipelineSettings.BusLimits());
            Log = CombatEventLog.Attach(Bus);
            Harness = new WorldHarness(
                map: TestCombat.OpenMap(TestCombat.MonsterMarker(0, monsterYCm)),
                events: new EventDispatchHook(Bus), bus: Bus,
                actions: ActionCatalog.From(Tables),
                combat: TestCombat.Rules(Tables, TestCombat.Point()),
                projectiles: GameServer.Combat.ProjectileRules.From(Tables));
            Harness.Characters.AutoCreateClassId = classId;
            Player = Harness.Join(1); // the join tick also spawns the monster
            Monster = Assert.Single(Harness.World.Entities.OfType<Monster>());
        }

        public ConfigSet Tables { get; }
        public EventBus Bus { get; }
        public CombatEventLog Log { get; }
        public WorldHarness Harness { get; }
        public Player Player { get; }
        public Monster Monster { get; }

        public ActionCatalog Catalog => Harness.World.Actions;

        public int NetId(string actionId) => Catalog.Find(actionId)!.NetId;

        /// <summary>The hotbar slot of <paramref name="actionId"/> in the class's row, or 0 for its normal attack.</summary>
        public byte SlotOf(string actionId)
        {
            var loadout = Player.Actions.Loadout;
            if (Catalog[loadout.AttackIndex].Id == actionId)
                return 0;
            for (byte slot = 1; slot <= ActionBindings.MaxSkillSlots; slot++)
            {
                if (loadout.SlotIndex(slot) >= 0 && Catalog[loadout.SlotIndex(slot)].Id == actionId)
                    return slot;
            }
            throw new InvalidOperationException($"{actionId} is not in the {loadout.ClassId} kit");
        }

        /// <summary>Presses the action (its slot, or left click for the normal attack) in the frame the next tick applies.</summary>
        public void Press(string actionId, ushort sequence = 1)
        {
            byte slot = SlotOf(actionId);
            var frame = slot == 0
                ? TestWorlds.Frame(sequence, facing: North, actions: InputActions.Attack)
                : TestWorlds.Frame(sequence, facing: North, skillSlot: slot);
            Harness.SendInputs(1, frame);
        }

        public List<Damaged> DamageOnMonster() =>
            Log.Events.OfType<Damaged>().Where(d => d.SourceId == Player.Id && d.TargetId == Monster.Id).ToList();

        public void TickUntil(Func<bool> done, int limit)
        {
            for (int i = 0; i < limit && !done(); i++)
                Harness.World.Tick();
            Assert.True(done(), $"not reached in {limit} ticks: player {Player.Movement.Position}, monster {Monster.Movement.Position}");
        }
    }

    [Theory]
    // Warrior (melee): each strike's shape reaches a demon (body radius 50 cm) this far in front.
    [InlineData("warrior", "attack.warrior", 150)]
    [InlineData("warrior", "skill.warrior.slash", 150)]
    [InlineData("warrior", "skill.warrior.headCut", 250)]
    [InlineData("warrior", "skill.warrior.piercingAttack", 300)]
    [InlineData("warrior", "skill.warrior.jumpStrike", 450)] // 4 m dash, then the 1.5 m circle at the landing point
    // Archer (bow): 10 m away, well inside the 20 m arrows.
    [InlineData("archer", "attack.archer", 1_000)]
    [InlineData("archer", "skill.archer.piercingShot", 1_000)]
    [InlineData("archer", "skill.archer.heavyShot", 1_000)] // after its 1 s channel
    public void A_kit_action_damages_the_demon_in_front(string classId, string actionId, int monsterYCm)
    {
        var s = new Setup(classId, monsterYCm);
        Assert.Equal(classId, s.Player.Actions.Loadout.ClassId);
        int hp = s.Monster.Combatant!.Hp(s.Harness.World.NowMs);

        s.Press(actionId);
        s.TickUntil(() => s.DamageOnMonster().Count > 0, limit: 60);

        var hit = Assert.Single(s.DamageOnMonster());
        Assert.Equal(s.NetId(actionId), hit.ActionId);
        Assert.True(hit.Amount > 0);
        Assert.Equal(hp - hit.Amount, s.Monster.Combatant.Hp(s.Harness.World.NowMs));
    }

    [Fact]
    public void Multi_shot_spawns_three_arrows_that_each_hit_the_demon()
    {
        // Three Projectile effects at 0, 150 and 300 ms of Active (ticks 0, 3 and 6 of it), within the per-owner cap of 8.
        var s = new Setup("archer", 1_000);
        var projectiles = s.Harness.World.Projectiles!;
        long spawned = projectiles.Spawned;
        s.Press("skill.archer.multiShot");
        s.TickUntil(() => s.DamageOnMonster().Count == 3, limit: 60);

        Assert.Equal(3, projectiles.Spawned - spawned);
        Assert.All(s.DamageOnMonster(), d => Assert.Equal(s.NetId("skill.archer.multiShot"), d.ActionId));
    }

    [Theory]
    [InlineData("skill.archer.backStep", -400)] // 180 degrees from the facing
    [InlineData("skill.archer.dash", 500)] // along the facing (TODO(open): the dodge's direction rule)
    public void The_archers_dashes_move_it_their_distance_and_direction(string actionId, int expectedYCm)
    {
        var s = new Setup("archer", 3_000);
        var start = s.Player.Movement.Position;
        var action = s.Catalog.Find(actionId)!;
        s.Press(actionId);
        s.Harness.Ticks(action.TotalTicks + 1);

        Assert.Equal(start + FixVec2.FromCm(0, expectedYCm), s.Player.Movement.Position);
        Assert.Equal(3, s.Player.Actions.Charges(ChargePool.Dodge)); // the Dash uses no dodge charge
        Assert.Empty(s.DamageOnMonster());
    }

    [Fact]
    public void A_skill_without_enough_mp_is_refused_with_NoMp_and_spends_nothing()
    {
        // R-3.1-04: Slash costs 20 stamina and 3 MP; the warrior keeps 2 of its 30 MP.
        var s = new Setup("warrior", 3_000);
        var meters = s.Player.Combatant!.Meters;
        long now = s.Harness.World.NowMs;
        Assert.Equal(SpendResult.Ok, meters.TrySpend(ResourceKind.Mp, meters.Current(ResourceKind.Mp, now) - 2, now));
        int stamina = meters.Current(ResourceKind.Stamina, now);

        s.Press("skill.warrior.slash", sequence: 5);
        s.Harness.Ticks(1);

        Assert.Equal([((ushort)5, (ushort)s.NetId("skill.warrior.slash"), (byte)7, ActionReason.NoMp)], s.Harness.Transport.ActionResultsFor(1));
        Assert.False(s.Player.Actions.IsBusy);
        now = s.Harness.World.NowMs;
        Assert.Equal(stamina, meters.Current(ResourceKind.Stamina, now)); // full already, and nothing was spent
        Assert.Equal(2, meters.Current(ResourceKind.Mp, now));
        Assert.Equal(0, s.Player.Actions.CooldownTicks(s.Catalog.Find("skill.warrior.slash")!.Index));
    }

    [Fact]
    public void The_warriors_empty_slot_1_is_refused_as_not_learned()
    {
        // The Stamina buff comes with the statuses (wave 5); until then slot 1 is empty.
        var s = new Setup("warrior", 3_000);
        s.Harness.SendInputs(1, TestWorlds.Frame(3, facing: North, skillSlot: 1));
        s.Harness.Ticks(1);
        Assert.Equal([((ushort)3, (ushort)0, (byte)1, ActionReason.SkillNotLearned)], s.Harness.Transport.ActionResultsFor(1));
    }
}
