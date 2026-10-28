using GameServer.Entities;
using GameServer.Events;
using GameServer.Items;
using GameServer.Persistence;
using GameServer.Progression;
using GameServer.Tests.Support;
using GameServer.World;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Actions;
using Shared.Simulation.Skills;

namespace GameServer.Tests.Skills;

/// <summary>
/// The skill tree in the world tick by tick (spec 3.5, 9.6; WP-M2-5) with the repository tables: requests in phase 1,
/// refusals answered at once, value operations answered, announced and made visible only once their save committed
/// (rule 5), the loadout built from the learned skills the player placed, the class advance (R-3.5-01) with its stats and
/// armor, passives, the combat blocks (R-3.5-04, user decision 2026-10-27) and weapon requirements (R-3.5-07).
/// </summary>
public class SkillTreeWorldTests
{
    private const ushort North = 0;

    private sealed class Fixture : IEventConsumer<SkillLearned>, IEventConsumer<SkillEvolved>, IEventConsumer<Respecced>,
        IEventConsumer<ClassAdvanced>
    {
        public Fixture(string classId = "warrior", int level = 1, int? points = null, string[]? learned = null, bool grantStarter = false)
        {
            var tables = TestCombat.Tables();
            Catalog = ActionCatalog.From(tables);
            Rules = SkillRules.From(tables)!;
            Bus = new EventBus(1, new FakeClock(), TestPipelineSettings.BusLimits());
            Bus.Subscribe<SkillLearned>(this);
            Bus.Subscribe<SkillEvolved>(this);
            Bus.Subscribe<Respecced>(this);
            Bus.Subscribe<ClassAdvanced>(this);
            Harness = new WorldHarness(map: TestCombat.OpenMap(TestCombat.MonsterMarker(0, 150)), events: new EventDispatchHook(Bus),
                bus: Bus, actions: Catalog, combat: TestCombat.Rules(tables, TestCombat.Point()), items: ItemRules.From(tables),
                progression: ProgressionRules.From(tables), skills: Rules);
            Character = Harness.Characters.Add(1, 0, new CharacterState(level, 0, "", 0, 0, 0, 0, 100, 50, 100, SkillPoints: points ?? level),
                classId: classId);
            if (learned != null)
                Harness.Characters.Skills[Character.CharacterId] =
                    new SkillRecord(learned.Select(n => new LearnedSkill(n, 1)).ToList(), HotbarJson: null);
            if (grantStarter)
                Harness.Characters.NoStarterYet.Add(Character.CharacterId);
            Player = Harness.Join(1);
            Harness.Ticks(1);
        }

        public WorldHarness Harness { get; }
        public ActionCatalog Catalog { get; }
        public SkillRules Rules { get; }
        public EventBus Bus { get; }
        public CharacterRecord Character { get; }
        public Player Player { get; }
        public long Id => Character.CharacterId;
        public GameWorld World => Harness.World;
        public FakeTransport Transport => Harness.Transport;
        public List<object> Events { get; } = [];

        public void Handle(in EventEnvelope envelope, in SkillLearned payload) => Events.Add(payload);
        public void Handle(in EventEnvelope envelope, in SkillEvolved payload) => Events.Add(payload);
        public void Handle(in EventEnvelope envelope, in Respecced payload) => Events.Add(payload);
        public void Handle(in EventEnvelope envelope, in ClassAdvanced payload) => Events.Add(payload);

        private uint _requests;

        /// <summary>Sends a request and runs the ticks for its save and the answer (saves complete at once by default).</summary>
        public SkillTreeResult Ask(SkillTreeOp op, string target = "", byte slot = 0, int ticks = 4)
        {
            Harness.SendSkillTree(1, ++_requests, op, target, slot);
            Harness.Ticks(ticks);
            return Transport.SkillResultsFor(1).Last(r => r.RequestId == _requests);
        }

        public SkillTreeState LastTree() => Transport.SkillTreesFor(1)[^1];

        /// <summary>Releases the keys, then presses a hotbar slot in the frame the next tick applies (a new press), then ticks once.</summary>
        public void Press(byte slot, ushort sequence)
        {
            Harness.SendInputs(1, TestWorlds.Frame((ushort)(sequence * 2), facing: North));
            Harness.Ticks(1);
            Harness.SendInputs(1, TestWorlds.Frame((ushort)(sequence * 2 + 1), facing: North, skillSlot: slot));
            Harness.Ticks(1);
        }

        public string Running() => Player.Actions.ActionIndex >= 0 ? Catalog[Player.Actions.ActionIndex].Id : "";

        public int Def() => Player.Combatant!.Stats.Get(World.Combat!.Config.Ids.Def);

        /// <summary>The next class 2 batch (every second) and its completion.</summary>
        public void Save()
        {
            Harness.Clock.Advance(1_000);
            Harness.Ticks(3);
        }
    }

    [Fact]
    public void A_new_character_has_an_empty_tree_and_its_slot_presses_are_not_learned()
    {
        // R-3.5-04 (user decision 2026-10-27): a new character has its normal attack and 1 point, no kit.
        var f = new Fixture();
        var state = Assert.Single(f.Transport.SkillTreesFor(1));
        Assert.Equal(("warrior", 0, 0, SkillTreeCause.Initial), (state.ClassId, state.Nodes.Length, state.Hotbar.Length, state.Cause));
        f.Press(9, 1);
        Assert.Contains(f.Transport.ActionResultsFor(1), r => r.Reason == ActionReason.SkillNotLearned && r.Slot == 9);
    }

    [Fact]
    public void A_learned_skill_is_answered_and_pressable_only_once_its_save_committed()
    {
        var f = new Fixture();
        f.Harness.Saves.AutoComplete = false;
        f.Harness.SendSkillTree(1, 1, SkillTreeOp.Learn, "warrior.piercingAttack");
        f.Harness.Ticks(3);
        Assert.Empty(f.Transport.SkillResultsFor(1)); // rule 5: no answer before the commit
        Assert.Equal(-1, f.Player.Actions.Loadout.SlotIndex(9));
        Assert.Equal(0, f.World.Progression!.Snapshot(f.Id).SkillPoints); // spent in memory, saved with the batch
        var job = Assert.Single(f.Harness.Saves.Jobs, j => j.Skills is { Ops.Count: 1 });
        Assert.Equal((SaveReason.SkillTree, 0), (job.Reason, job.State.SkillPoints));

        f.Harness.Saves.CompleteAll();
        f.Harness.Ticks(2);

        var answer = Assert.Single(f.Transport.SkillResultsFor(1));
        Assert.Equal((SkillTreeReason.None, SkillTreeOp.Learn, "warrior.piercingAttack", (byte)1), (answer.Reason, answer.Op, answer.Target, answer.Level));
        var tree = f.LastTree();
        Assert.Equal(SkillTreeCause.Changed, tree.Cause);
        Assert.Equal(("warrior.piercingAttack", (byte)1), (tree.Nodes.Single().NodeId, tree.Nodes.Single().Level));
        Assert.Equal((byte)9, tree.Hotbar.Single().Slot); // its kit slot, R (the default layout)
        var learned = Assert.Single(f.Events.OfType<SkillLearned>());
        Assert.Equal((f.Id, "warrior.piercingAttack", 1, 1), (learned.CharacterId, learned.NodeId, learned.Level, learned.PointsSpent));
        Assert.Contains(f.Transport.ProgressionFor(1), p => p is { Cause: ProgressionCause.SkillPoints, SkillPoints: 0 });
        // The answer comes before the state that shows it (spec 9.13 pattern).
        var ids = f.Transport.ReliableIdsFor(1);
        Assert.True(ids.LastIndexOf(ReliableMessageId.SkillTreeResult) < ids.LastIndexOf(ReliableMessageId.SkillTreeState));

        f.Press(9, 2);
        Assert.Equal("skill.warrior.piercingAttack", f.Running());
    }

    [Fact]
    public void Refusals_are_answered_at_once_and_a_repeated_request_gets_the_first_answer()
    {
        var f = new Fixture();
        f.Harness.Saves.AutoComplete = false;
        Assert.Equal(SkillTreeReason.CharacterLevelTooLow, f.Ask(SkillTreeOp.Learn, "warrior.headCut", ticks: 1).Reason);
        Assert.Equal(SkillTreeReason.SkillUnavailable, f.Ask(SkillTreeOp.Learn, "warrior.provoke", ticks: 1).Reason);
        Assert.Equal(SkillTreeReason.SkillNotLearned, f.Ask(SkillTreeOp.Upgrade, "warrior.piercingAttack", ticks: 1).Reason);
        Assert.Equal(SkillTreeReason.AdvanceLevelTooLow, f.Ask(SkillTreeOp.Advance, "knight", ticks: 1).Reason);

        f.Harness.SendSkillTree(1, 50, SkillTreeOp.Learn, "warrior.headCut");
        f.Harness.SendSkillTree(1, 50, SkillTreeOp.Learn, "warrior.piercingAttack"); // the same id again: the first answer
        f.Harness.Ticks(1);
        var answers = f.Transport.SkillResultsFor(1).Where(a => a.RequestId == 50).ToList();
        Assert.Equal(2, answers.Count);
        Assert.All(answers, a => Assert.Equal((SkillTreeReason.CharacterLevelTooLow, "warrior.headCut"), (a.Reason, a.Target)));
        Assert.Equal(0, f.World.Skills!.Working(f.Id)!.LearnedCount);

        var nine = new Fixture(level: 9, points: 0);
        Assert.Equal(SkillTreeReason.PrerequisiteMissing, nine.Ask(SkillTreeOp.Learn, "warrior.jumpStrike", ticks: 1).Reason);
        Assert.Equal(SkillTreeReason.NotEnoughPoints, nine.Ask(SkillTreeOp.Learn, "warrior.piercingAttack", ticks: 1).Reason);
    }

    [Fact]
    public void A_level_30_warrior_advances_to_knight_learns_shield_strike_and_uses_it_from_the_hotbar()
    {
        // Done criterion of WP-M2-5 in the world: R-3.5-01 advance, a tier-1 node at 30 + 5 x 0, on the bar, pressed.
        var f = new Fixture(level: 30, points: 30, learned: ["warrior.piercingAttack"]);
        var combatant = f.Player.Combatant!;
        Assert.Equal("warrior", combatant.TemplateId);

        Assert.Equal(SkillTreeReason.None, f.Ask(SkillTreeOp.Advance, "knight").Reason);

        var advanced = Assert.Single(f.Events.OfType<ClassAdvanced>());
        Assert.Equal((f.Id, "warrior", "knight", 1), (advanced.CharacterId, advanced.FromClassId, advanced.ToClassId, advanced.Tier));
        Assert.Equal(("knight", ArmorClass.Heavy), (combatant.TemplateId, combatant.ArmorClass));
        Assert.Equal("knight", f.Harness.Characters.Get(f.Id).ClassId); // saved with the batch
        Assert.Equal(1, f.World.Skills!.Visible(f.Id)!.LevelOf("warrior.piercingAttack")); // nothing reset (R-3.5-04)
        Assert.Equal("knight", f.LastTree().ClassId);

        Assert.Equal(SkillTreeReason.None, f.Ask(SkillTreeOp.Learn, "knight.shieldStrike").Reason);
        Assert.Equal(SkillTreeReason.None, f.Ask(SkillTreeOp.AssignSlot, "knight.shieldStrike", slot: 7).Reason);
        Assert.Equal(7, f.World.Skills.Visible(f.Id)!.SlotOf("knight.shieldStrike"));
        Assert.Equal(29, f.World.Progression!.Snapshot(f.Id).SkillPoints); // the advance is free, the node costs 1

        f.Press(7, 1);
        Assert.Equal("skill.knight.shieldStrike", f.Running());
        Assert.Equal(SkillTreeReason.NotNextClass, f.Ask(SkillTreeOp.Advance, "swordman").Reason); // the class is permanent
    }

    [Fact]
    public void An_evolution_replaces_its_base_on_its_slot_and_the_evolved_skill_presses_the_inherited_action()
    {
        var f = new Fixture(classId: "knight", level: 30, points: 30, learned: ["warrior.piercingAttack"]);
        Assert.Equal(9, f.World.Skills!.Visible(f.Id)!.SlotOf("warrior.piercingAttack"));

        Assert.Equal(SkillTreeReason.None, f.Ask(SkillTreeOp.Evolve, "knight.piercingSequence").Reason);

        var evolved = Assert.Single(f.Events.OfType<SkillEvolved>());
        Assert.Equal(("warrior.piercingAttack", "knight.piercingSequence"), (evolved.FromNodeId, evolved.NodeId));
        var state = f.World.Skills.Visible(f.Id)!;
        Assert.Equal((0, 1, 9), (state.LevelOf("warrior.piercingAttack"), state.LevelOf("knight.piercingSequence"), state.SlotOf("knight.piercingSequence")));
        f.Press(9, 1);
        Assert.Equal("skill.warrior.piercingAttack", f.Running()); // R-3.5-05: inherited until its own action (PR 2)
    }

    [Fact]
    public void Advance_evolve_and_respec_are_refused_in_combat_but_learning_is_not()
    {
        var f = new Fixture(classId: "knight", level: 30, points: 30, learned: ["warrior.piercingAttack"]);
        f.Player.LastCombatAtMs = f.Harness.Clock.UtcNowMs;
        Assert.Equal(SkillTreeReason.RespecBlockedCombat, f.Ask(SkillTreeOp.Respec).Reason);
        Assert.Equal(SkillTreeReason.EvolveBlockedCombat, f.Ask(SkillTreeOp.Evolve, "knight.piercingSequence").Reason);
        Assert.Equal(SkillTreeReason.None, f.Ask(SkillTreeOp.Learn, "knight.shieldStrike").Reason);

        var warrior = new Fixture(level: 30, points: 30);
        warrior.Player.LastCombatAtMs = warrior.Harness.Clock.UtcNowMs;
        Assert.Equal(SkillTreeReason.AdvanceBlockedCombat, warrior.Ask(SkillTreeOp.Advance, "knight").Reason);
    }

    [Fact]
    public void A_passive_adds_its_stat_per_level_and_respec_gives_the_points_back_and_removes_it()
    {
        var f = new Fixture(level: 9, points: 9);
        int def = f.Def();
        Assert.Equal(SkillTreeReason.None, f.Ask(SkillTreeOp.Learn, "warrior.increaseDef").Reason);
        Assert.Equal(def + 2, f.Def());
        Assert.Equal(SkillTreeReason.None, f.Ask(SkillTreeOp.Upgrade, "warrior.increaseDef").Reason);
        Assert.Equal(def + 4, f.Def()); // times the level (user decision 2026-10-27)
        Assert.Equal(7, f.World.Progression!.Snapshot(f.Id).SkillPoints);

        Assert.Equal(SkillTreeReason.None, f.Ask(SkillTreeOp.Respec).Reason);

        Assert.Equal(def, f.Def());
        Assert.Equal(9, f.World.Progression.Snapshot(f.Id).SkillPoints);
        var respec = Assert.Single(f.Events.OfType<Respecced>());
        Assert.Equal((1, 2), (respec.NodesCleared, respec.PointsRefunded));
    }

    [Fact]
    public void Combat_training_sets_the_knight_s_armor_to_medium()
    {
        // R-3.5-08: a learned tree passive can set another tier.
        var f = new Fixture(classId: "knight", level: 35, points: 35); // row 1 of a tier-1 tree: 30 + 5 x 1
        Assert.Equal(ArmorClass.Heavy, f.Player.Combatant!.ArmorClass);
        Assert.Equal(SkillTreeReason.None, f.Ask(SkillTreeOp.Learn, "knight.combatTraning").Reason);
        Assert.Equal(ArmorClass.Medium, f.Player.Combatant.ArmorClass);
    }

    [Fact]
    public void A_bow_skill_is_refused_with_WrongWeapon_until_the_bow_is_equipped_and_committed()
    {
        // R-3.5-07: a skill whose weapon is not equipped stays learned and on the bar; its press is refused.
        var f = new Fixture(classId: "archer", learned: ["archer.piercingShot"], grantStarter: true);
        Assert.Equal(7, f.World.Skills!.Visible(f.Id)!.SlotOf("archer.piercingShot"));
        Assert.Equal(WeaponMasks.Of(new[] { WeaponType.Bow, WeaponType.DualCrossbows }), f.Player.Actions.Loadout.SlotWeapons(7));
        f.Press(7, 1);
        Assert.Contains(f.Transport.ActionResultsFor(1), r => r.Reason == ActionReason.WrongWeapon && r.Slot == 7);

        f.Save(); // the starter bow's grant commits: EquipmentChanged
        f.Press(7, 2);
        Assert.Equal("skill.archer.piercingShot", f.Running());
    }

    [Fact]
    public void A_hotbar_change_is_visible_at_once_and_only_learned_hotbar_skills_go_on_the_bar()
    {
        var f = new Fixture(level: 9, points: 9, learned: ["warrior.piercingAttack", "warrior.increaseDef"]);
        Assert.Equal(SkillTreeReason.NotForHotbar, f.Ask(SkillTreeOp.AssignSlot, "warrior.increaseDef", slot: 2, ticks: 1).Reason);
        Assert.Equal(SkillTreeReason.SkillNotLearned, f.Ask(SkillTreeOp.AssignSlot, "warrior.slash", slot: 2, ticks: 1).Reason);
        Assert.Equal(SkillTreeReason.HotbarSlotInvalid, f.Ask(SkillTreeOp.AssignSlot, "warrior.piercingAttack", slot: 12, ticks: 1).Reason);

        Assert.Equal(SkillTreeReason.None, f.Ask(SkillTreeOp.AssignSlot, "warrior.piercingAttack", slot: 2, ticks: 1).Reason);
        Assert.True(f.Player.Actions.Loadout.SlotIndex(2) >= 0);
        Assert.Equal(-1, f.Player.Actions.Loadout.SlotIndex(9));
        Assert.Equal(SkillTreeReason.None, f.Ask(SkillTreeOp.ClearSlot, slot: 2, ticks: 1).Reason);
        Assert.Equal(-1, f.Player.Actions.Loadout.SlotIndex(2));
        Assert.Empty(f.LastTree().Hotbar);
    }

    [Fact]
    public void A_world_without_the_skill_tables_answers_unavailable_and_keeps_the_class_kit()
    {
        var harness = new WorldHarness(items: ItemRules.From(TestCombat.Tables()), actions: ActionCatalog.From(TestCombat.Tables()),
            combat: TestCombat.Rules());
        var player = harness.Join(1);
        Assert.True(player.Actions.Loadout.SlotIndex(7) >= 0); // the warrior's M1 kit (ActionBindings)
        harness.SendSkillTree(1, 1, SkillTreeOp.Learn, "warrior.slash");
        harness.Ticks(2);
        Assert.Equal(SkillTreeReason.Unavailable, Assert.Single(harness.Transport.SkillResultsFor(1)).Reason);
    }
}
