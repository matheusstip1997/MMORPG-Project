using GameServer.Combat;
using GameServer.Entities;
using GameServer.Events;
using GameServer.Items;
using GameServer.Persistence;
using GameServer.Progression;
using GameServer.Tests.Combat;
using GameServer.Tests.Support;
using GameServer.World.Map;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;
using Shared.Simulation.Actions;
using Shared.Simulation.Skills;
using Shared.Simulation.Stats;

namespace GameServer.Tests.Skills;

/// <summary>
/// WP-M2-9 through the full tick with the committed tables (spec 3.5 "M2 wave 1 decisions", 3.7 "M2 status list", 3.8
/// "M2 numbers"; PLACEHOLDER numbers): every mechanic the shipping skill rows name, from the learned node on the hotbar to
/// what lands. The player stands at the origin facing north (+Y); the demons stand where each test puts them.
/// </summary>
public class MechanicsWorldTests
{
    private const ushort North = 0;
    private const byte Slot = 2;
    private const int Level = 50;

    private sealed class Setup
    {
        /// <param name="monsters">Where each monster stands (cm); a demon unless <paramref name="monsterId"/> says otherwise.</param>
        /// <param name="aggro">False cuts the aggro range to 1 cm (the monsters stay put unless hit).</param>
        public Setup(string classId, string? nodeId, (int X, int Y)[] monsters, bool aggro = false,
            string monsterId = TestCombat.MonsterId)
        {
            Tables = TestCombat.Tables();
            Assert.True(Tables.TryGet(typeof(MonsterDef), out var defs));
            // A level-50 hit kills a demon outright: the monsters get a large HP pool so what lands can be seen.
            defs.Rows.Cast<MonsterDef>().Single(m => m.Id == monsterId).MaxHp = 1_000_000;
            if (!aggro)
                defs.Rows.Cast<MonsterDef>().Single(m => m.Id == monsterId).AggroRangeCm = 1;
            Rules = SkillRules.From(Tables)!;
            Bus = new EventBus(1, new FakeClock(), TestPipelineSettings.BusLimits());
            Log = CombatEventLog.Attach(Bus);
            Statuses = StatusEventLog.Attach(Bus);
            var markers = monsters.Select((m, i) => TestCombat.MonsterMarker(m.X, m.Y, id: "test.spawn." + i)).ToArray();
            Harness = new WorldHarness(map: TestCombat.OpenMap(markers), events: new EventDispatchHook(Bus), bus: Bus,
                actions: ActionCatalog.From(Tables),
                combat: TestCombat.Rules(Tables, markers.Select(m => TestCombat.Point(markerId: m.Id, monsterId: monsterId)).ToArray()),
                items: ItemRules.From(Tables), progression: ProgressionRules.From(Tables), skills: Rules,
                projectiles: ProjectileRules.From(Tables));
            var character = Harness.Characters.Add(1, 0, new CharacterState(Level, 0, "", 0, 0, 0, 0, 100, 50, 100, SkillPoints: 0),
                classId: classId);
            if (nodeId != null)
                Harness.Characters.Skills[character.CharacterId] = new SkillRecord([new LearnedSkill(nodeId, 1)], HotbarJson: null);
            Player = Harness.Join(1);
            Harness.Ticks(1);
            Monsters = Harness.World.Entities.OfType<Monster>().OrderBy(m => m.Id).ToList();
            Assert.Equal(monsters.Length, Monsters.Count);
        }

        public ConfigSet Tables { get; }
        public SkillRules Rules { get; }
        public EventBus Bus { get; }
        public CombatEventLog Log { get; }
        public StatusEventLog Statuses { get; }
        public WorldHarness Harness { get; }
        public Player Player { get; }
        public List<Monster> Monsters { get; }
        public Monster Monster => Monsters[0];
        public GameServer.World.GameWorld World => Harness.World;
        public long NowMs => World.NowMs;
        public uint Tick => World.ServerTick;

        public StatusDefinition Status(string id)
        {
            Assert.True(World.Combat!.Statuses.TryGet(id, out var definition));
            return definition;
        }

        /// <summary>Learns, places and presses the node (as <c>SkillContentWorldTests</c>); returns its action.</summary>
        public ActionTiming Use(string nodeId)
        {
            Harness.SendSkillTree(1, 1, SkillTreeOp.AssignSlot, nodeId, Slot);
            Harness.Ticks(4);
            Assert.Equal(SkillTreeReason.None, Harness.Transport.SkillResultsFor(1).Last().Reason);
            Harness.SendInputs(1, TestWorlds.Frame(2, facing: North));
            Harness.Ticks(1);
            Harness.SendInputs(1, TestWorlds.Frame(3, facing: North, skillSlot: Slot));
            Harness.Ticks(1);
            Assert.Empty(Harness.Transport.ActionResultsFor(1));
            var action = World.Actions[Player.Actions.ActionIndex];
            Assert.Equal(Rules.Skill(nodeId)!.ActionId, action.Id);
            return action;
        }

        public void TicksUntil(Func<bool> done, int limit = 200)
        {
            for (int i = 0; i < limit && !done(); i++)
                Harness.Ticks(1);
            Assert.True(done(), "not reached");
        }

        public int StacksOn(Entity target, string statusId)
        {
            var statuses = target.Combatant!.Statuses;
            for (int i = 0; i < statuses.Count; i++)
            {
                if (statuses[i].Definition.Id == statusId)
                    return statuses[i].Stacks;
            }
            return 0;
        }
    }

    [Fact]
    public void Heavy_strike_stuns_the_demon_for_half_a_second()
    {
        // Swordman Heavy Strike: the 2 m circle carries the on-hit stunned (500 ms, spec 3.5 wave 1 decisions).
        var s = new Setup("swordman", "swordman.heavyStrike", [(0, 150)]);
        s.Use("swordman.heavyStrike");
        s.TicksUntil(() => s.Monster.Combatant!.Statuses.Has("stunned"));

        Assert.Equal(ActionState.Stunned, s.Monster.Actions.State);
        Assert.True(s.Monster.Actions.IsBusy); // no movement and no attack while it lasts
        uint stunnedAt = s.Tick;
        s.TicksUntil(() => !s.Monster.Combatant!.Statuses.Has("stunned"), limit: 20);
        Assert.Equal(s.Status("stunned").DurationTicks, (int)(s.Tick - stunnedAt));
        Assert.NotEqual(ActionState.Stunned, s.Monster.Actions.State);
    }

    [Fact]
    public void A_blinded_demons_claw_hits_nothing_and_flash_blinds_the_demons_it_hits()
    {
        // The demon chases and claws the player; blinded as its claw starts, the claw lands on nobody (spec 3.7).
        var s = new Setup("assist", null, [(0, 400)], aggro: true);
        int hp = s.Player.Combatant!.Hp(s.NowMs);
        s.TicksUntil(() => s.Monster.Actions.State == ActionState.Startup);
        s.World.Statuses!.Apply(s.Player, s.Monster, s.Status("blinded"), s.Tick);
        s.TicksUntil(() => !s.Monster.Actions.IsBusy, limit: 60);
        s.Harness.Ticks(10); // past any pending hit (R-3.1-03)
        Assert.DoesNotContain(s.Log.Events.OfType<Damaged>(), d => d.SourceId == s.Monster.Id);
        Assert.Equal(hp, s.Player.Combatant.Hp(s.NowMs));

        // Assist Flash: the holy circle blinds each enemy it hits for 1 s.
        var flash = new Setup("assist", "assist.flash", [(0, 200)]);
        flash.Use("assist.flash");
        flash.TicksUntil(() => flash.Monster.Combatant!.Statuses.Has("blinded"), limit: 60);
        Assert.Contains(flash.Log.Events.OfType<Damaged>(), d => d.TargetId == flash.Monster.Id);
    }

    [Fact]
    public void Piercing_sequence_stacks_defence_down_to_three()
    {
        var s = new Setup("knight", "knight.piercingSequence", [(0, 300)]);
        int def = s.Monster.Combatant!.Stats.Get(s.World.Combat!.Config.Ids.Def);
        var action = s.Use("knight.piercingSequence");
        s.Harness.Ticks(action.TotalTicks);

        Assert.Equal(3, s.StacksOn(s.Monster, "defenceDown")); // one per thrust, at most 3 (StatusSettings)
        Assert.Equal(def * 70 / 100, s.Monster.Combatant.Stats.Get(s.World.Combat.Config.Ids.Def));
        Assert.False(s.Monster.Combatant.Statuses.Has("weakened")); // the stand-in is gone
    }

    [Theory]
    [InlineData("archer", "archer.bombThrow")]
    [InlineData("mage", "mage.rockThrow")]
    public void An_exploding_throw_hits_the_demon_it_strikes_and_the_one_beside_it(string classId, string nodeId)
    {
        // The projectile hits the first demon; the 1.5 m explosion where it struck reaches the second (beside the path,
        // out of the projectile's own reach).
        var s = new Setup(classId, nodeId, [(0, 1_000), (120, 1_000)]);
        var action = s.Use(nodeId);
        s.TicksUntil(() => s.Monsters.All(m => s.Log.Events.OfType<Damaged>().Any(d => d.TargetId == m.Id)), limit: 120);

        var hits = s.Log.Events.OfType<Damaged>().Where(d => d.SourceId == s.Player.Id).ToList();
        Assert.All(hits, d => Assert.Equal(action.NetId, d.ActionId));
        Assert.Single(hits, d => d.TargetId == s.Monsters[0].Id); // the explosion skips the target the projectile hit
        Assert.Single(hits, d => d.TargetId == s.Monsters[1].Id);
    }

    [Fact]
    public void Blood_strike_fills_the_blood_loss_meter_and_its_trigger_takes_15_percent_of_max_hp()
    {
        var s = new Setup("swordman", "swordman.bloodStrike", [(0, 250)]);
        var buildUps = s.World.Combat!.BuildUps;
        int index = buildUps.IndexOf("bloodLoss");
        s.Monster.Combatant!.BuildUps[index].Gain(buildUps[index], 90, 0, s.Tick); // R-3.8-02: 90 of 100
        int maxHp = s.Monster.Combatant.MaxHp(s.NowMs);

        s.Use("swordman.bloodStrike");
        s.TicksUntil(() => s.Monster.Combatant.Statuses.Has("bloodLoss"), limit: 40);
        s.Harness.Ticks(2);

        var burst = Assert.Single(s.Log.Events.OfType<Damaged>(), d => d.ActionId == 0 && d.TargetId == s.Monster.Id);
        Assert.Equal(maxHp * 15 / 100, burst.Amount);
        Assert.Equal(s.Player.Id, burst.SourceId); // R-3.8-09
    }

    [Fact]
    public void Blood_loss_on_a_boss_deals_at_most_2_percent_of_its_max_hp()
    {
        // R-3.8-08 (spec 3.8 M2 numbers): MonsterDef.maxHpEffectCapPercent 2 on the cyclops.
        var s = new Setup("swordman", null, [(0, 2_000)], monsterId: "m1.cyclopsBoss");
        int maxHp = s.Monster.Combatant!.MaxHp(s.NowMs);
        s.World.Statuses!.Apply(s.Player, s.Monster, s.Status("bloodLoss"), s.Tick);
        s.Harness.Ticks(2);

        var burst = Assert.Single(s.Log.Events.OfType<Damaged>(), d => d.ActionId == 0 && d.TargetId == s.Monster.Id);
        Assert.Equal(maxHp * 2 / 100, burst.Amount);
    }

    [Fact]
    public void Simple_cleanse_removes_the_two_newest_debuffs_of_the_caster()
    {
        var s = new Setup("assist", "assist.simpleCleanse", [(0, 2_000)]);
        s.World.Statuses!.Apply(s.Monster, s.Player, s.Status("weakened"), s.Tick);
        s.World.Statuses.Apply(s.Monster, s.Player, s.Status("burn"), s.Tick);
        s.World.Statuses.Apply(s.Monster, s.Player, s.Status("defenceDown"), s.Tick);

        var action = s.Use("assist.simpleCleanse");
        s.Harness.Ticks(action.TotalTicks);

        Assert.True(s.Player.Combatant!.Statuses.Has("weakened")); // the oldest stays
        Assert.False(s.Player.Combatant.Statuses.Has("burn"));
        Assert.False(s.Player.Combatant.Statuses.Has("defenceDown"));
        Assert.Equal(2, s.Statuses.Events.OfType<EffectCleansed>().Count());
    }

    [Fact]
    public void A_cold_player_walks_30_percent_slower_on_the_server()
    {
        var s = new Setup("warrior", null, [(0, 3_000)]);
        int baseSpeed = s.World.Settings.MoveSpeedCmPerSec;
        ushort sequence = 10;
        long Walk()
        {
            var before = s.Player.Movement.Position;
            s.Harness.SendInputs(1, TestWorlds.Frame(sequence++, keys: Shared.Protocol.MoveKeys.D, facing: North));
            s.Harness.Ticks(1);
            return (s.Player.Movement.Position - before).X;
        }
        Assert.Equal(MovementSim.StepPerTick(baseSpeed, 20), Walk());

        s.World.Statuses!.Apply(s.Monster, s.Player, s.Status("cold"), s.Tick);

        Assert.Equal(MovementSim.StepPerTick(baseSpeed * 70 / 100, 20), Walk());
        Assert.Equal(0, s.World.SpeedViolations);
    }
}
