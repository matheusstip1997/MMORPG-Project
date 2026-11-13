using GameServer.Events;
using GameServer.Items;
using GameServer.Items.Mail;
using GameServer.Persistence;
using GameServer.Progression;
using GameServer.Skills;
using GameServer.Tests.Support;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Actions;
using Shared.Simulation.Items;
using Shared.Simulation.Skills;

namespace GameServer.Tests.Items;

/// <summary>
/// Armor per tier in the world tick by tick (spec 4.1 R-4.1-05, 3.5 R-3.5-08, M4 decisions 9a and 9b; WP-M4-13) with the
/// repository tables: the equip check reads the character's tier (the class row and the learned armor passives), and a skill
/// operation that changes the tier takes the worn pieces of the old tier off in its own save: into the Safe bag, by the
/// character's own mail what does not fit, all answered after the commit (rule 5). Parry is untouched: it reads the same tier.
/// </summary>
public class ArmorTierWorldTests
{
    private const byte Safe = 2, Equipped = 5;
    private const byte Head = 2, Chest = 3, Legs = 4;
    private const string IronHelm = "m2.ironHelm", IronCuirass = "m2.ironCuirass", LeatherVest = "m2.leatherVest",
        LeatherCap = "m2.leatherCap", ClothShirt = "m2.clothShirt", ClothPants = "m2.clothPants", CombatTraining = "knight.combatTraning";

    private sealed class Fixture : IEventConsumer<EquipmentChanged>, IEventConsumer<ItemTransferred>
    {
        public Fixture(string classId, int level, string[]? learned = null, params ItemInstanceRow[] instances)
            : this(classId, level, learned, withMail: true, instances)
        {
        }

        public Fixture(string classId, int level, string[]? learned, bool withMail, params ItemInstanceRow[] instances)
        {
            var tables = TestCombat.Tables();
            Items = ItemRules.From(tables)!;
            Mail = MailRules.From(tables, Items)!;
            Bus = new EventBus(1, new FakeClock(), TestPipelineSettings.BusLimits());
            Bus.Subscribe<EquipmentChanged>(this);
            Bus.Subscribe<ItemTransferred>(this);
            Harness = new WorldHarness(map: TestCombat.OpenMap(TestCombat.MonsterMarker(0, 2_000)), events: new EventDispatchHook(Bus),
                bus: Bus, actions: ActionCatalog.From(tables), combat: TestCombat.Rules(tables, TestCombat.Point()), items: Items,
                progression: ProgressionRules.From(tables), skills: SkillRules.From(tables), mail: withMail ? Mail : null);
            Character = Harness.Characters.Add(1, 0, new CharacterState(level, 0, "", 0, 0, 0, 0, 100, 50, 100, SkillPoints: level),
                classId: classId);
            Harness.Characters.Instances[Character.CharacterId] = instances;
            if (learned != null)
                Harness.Characters.Skills[Character.CharacterId] =
                    new SkillRecord(learned.Select(n => new LearnedSkill(n, 1)).ToList(), HotbarJson: null);
            Player = Harness.Join(1);
            Harness.Ticks(1);
        }

        public WorldHarness Harness { get; }
        public ItemRules Items { get; }
        public MailRules Mail { get; }
        public EventBus Bus { get; }
        public CharacterRecord Character { get; }
        public long Id => Character.CharacterId;
        public InventoryService Inventory => Harness.World.Inventory!;
        public FakeTransport Transport => Harness.Transport;
        public List<EquipmentChanged> Changed { get; } = [];
        public List<ItemTransferred> Transferred { get; } = [];

        public void Handle(in EventEnvelope envelope, in EquipmentChanged payload) => Changed.Add(payload);
        public void Handle(in EventEnvelope envelope, in ItemTransferred payload) => Transferred.Add(payload);

        public ItemStack At(byte container, int slot) => Inventory.CurrentInventory(Id).At(new InventoryLocation(container, slot));

        private uint _requests;

        /// <summary>Sends a skill request and runs the ticks for its save and the answer.</summary>
        public SkillTreeResult Ask(SkillTreeOp op, string target = "", int ticks = 4)
        {
            Harness.SendSkillTree(1, ++_requests, op, target);
            Harness.Ticks(ticks);
            return Transport.SkillResultsFor(1).Last(r => r.RequestId == _requests);
        }

        public InventoryMoveReason Equip(uint requestId, byte fromSlot, byte slot)
        {
            Harness.SendMove(1, requestId, Safe, fromSlot, Equipped, slot);
            Harness.Ticks(1);
            Harness.Clock.Advance(1_000);
            Harness.Ticks(3);
            return Transport.MoveResultsFor(1)[^1].Reason;
        }

        public GameServer.Entities.Player Player { get; }

        public ArmorClass Parry => Player.Combatant!.ArmorClass;
    }

    private static ItemInstanceRow Gear(long seq, byte container, int slot, string item) =>
        new(ItemInstanceIds.Make(1, seq), container, slot, item, 0, false, false, ItemInstanceIds.NewProperties(1));

    /// <summary>The Safe bag's equipment area filled with swords from <paramref name="firstSeq"/> on (no free slot left).</summary>
    private static IEnumerable<ItemInstanceRow> FullSafeBag(ItemRules items, long firstSeq)
    {
        int slots = items.ReceiverOf(ItemCategory.Equipment)!.Slots;
        return Enumerable.Range(0, slots).Select(i => Gear(firstSeq + i, Safe, i, "m2.trainingSword"));
    }

    [Fact]
    public void A_warrior_equips_a_heavy_chest_and_is_refused_a_light_one()
    {
        // The WP-M4-13 done criterion: the equip check reads the class's tier (ClassDef.armorClass: warrior heavy).
        var f = new Fixture("warrior", 1, null, Gear(1, Safe, 0, IronCuirass), Gear(2, Safe, 1, ClothShirt));

        Assert.Equal(InventoryMoveReason.None, f.Equip(1, 0, Chest));
        Assert.Equal(InventoryMoveReason.ArmorTierMismatch, f.Equip(2, 1, Chest));
        Assert.Equal(ItemInstanceIds.Make(1, 1), f.At(Equipped, Chest).InstanceId);
        Assert.Equal(ItemInstanceIds.Make(1, 2), f.At(Safe, 1).InstanceId); // the refused piece stays
        Assert.Equal(ArmorClass.Heavy, f.Inventory.ArmorTierOf(f.Id));
    }

    [Fact]
    public void A_swap_out_of_the_chest_slot_onto_armor_of_another_tier_is_refused_too()
    {
        // The swap path (unequipping onto gear that fits the slot equips that gear): the same check.
        var f = new Fixture("warrior", 1, null, Gear(1, Equipped, Chest, IronCuirass), Gear(2, Safe, 0, ClothShirt));

        f.Harness.SendMove(1, requestId: 1, Equipped, Chest, Safe, 0);
        f.Harness.Ticks(1);

        Assert.Equal(InventoryMoveReason.ArmorTierMismatch, f.Transport.MoveResultsFor(1)[^1].Reason);
        Assert.Equal(ItemInstanceIds.Make(1, 1), f.At(Equipped, Chest).InstanceId);
    }

    [Fact]
    public void Learning_combat_training_takes_the_heavy_pieces_off_into_the_bag_in_the_skill_s_save()
    {
        // R-3.5-08, decision 9b: the Knight's Combat Training sets medium; the worn heavy pieces come off with it.
        var f = new Fixture("knight", 35, null, Gear(1, Equipped, Head, IronHelm), Gear(2, Equipped, Chest, IronCuirass),
            Gear(3, Safe, 0, LeatherVest));
        Assert.Equal(ArmorClass.Heavy, f.Parry);
        f.Harness.Saves.AutoComplete = false;

        f.Harness.SendSkillTree(1, 1, SkillTreeOp.Learn, CombatTraining);
        f.Harness.Ticks(2);

        // Applied: the pieces are off in the world at once, the equip check reads medium, nothing is answered or announced.
        Assert.True(f.At(Equipped, Head).IsEmpty && f.At(Equipped, Chest).IsEmpty);
        Assert.Equal(ArmorClass.Medium, f.Inventory.ArmorTierOf(f.Id));
        Assert.Empty(f.Transport.SkillResultsFor(1));
        Assert.Empty(f.Changed);
        Assert.Equal(ArmorClass.Heavy, f.Parry); // the parry rules see only what is committed
        var save = f.Harness.Saves.Jobs.Single(j => j.Skills is { Ops.Count: > 0 });
        Assert.Equal(SaveReason.SkillTree, save.Reason);
        string key = save.Skills!.Ops.Single().OpKey;
        Assert.Equal(new[] { ArmorTierSweep.UnequipKey(key, Head), ArmorTierSweep.UnequipKey(key, Chest) },
            save.Inventory!.Ops.Select(o => o.OpKey)); // one transaction (rule 5)
        Assert.All(save.Inventory.Ops, o => Assert.Equal(nameof(InventoryReason.ArmorTierChange), o.Reason));
        Assert.Null(save.Mail); // everything fit

        f.Harness.Saves.CompleteAll();
        f.Harness.Saves.AutoComplete = true;
        f.Harness.Ticks(2);

        Assert.Equal(SkillTreeReason.None, f.Transport.SkillResultsFor(1).Single().Reason);
        Assert.Equal(ArmorClass.Medium, f.Parry);
        Assert.Equal(new[] { Head, Chest }, f.Changed.Select(c => (byte)c.Slot).Order());
        var bag = Enumerable.Range(0, 4).Select(s => f.At(Safe, s).InstanceId).ToList();
        Assert.Equal(new[] { ItemInstanceIds.Make(1, 3), ItemInstanceIds.Make(1, 1), ItemInstanceIds.Make(1, 2), 0L }, bag);

        // Now a medium piece goes on and a heavy one is refused.
        Assert.Equal(InventoryMoveReason.None, f.Equip(2, 0, Chest));
        Assert.Equal(InventoryMoveReason.ArmorTierMismatch, f.Equip(3, 2, Chest));
    }

    [Fact]
    public void With_a_full_bag_the_pieces_go_to_the_own_mail_as_one_armor_return_in_the_same_save()
    {
        var f0 = new Fixture("warrior", 1);
        var bag = FullSafeBag(f0.Items, 10).ToArray();
        var f = new Fixture("knight", 35, null, [Gear(1, Equipped, Head, IronHelm), Gear(2, Equipped, Chest, IronCuirass), .. bag]);
        f.Harness.Saves.AutoComplete = false;

        f.Harness.SendSkillTree(1, 1, SkillTreeOp.Learn, CombatTraining);
        f.Harness.Ticks(2);

        var save = f.Harness.Saves.Jobs.Single(j => j.Skills is { Ops.Count: > 0 });
        string key = save.Skills!.Ops.Single().OpKey;
        var send = Assert.Single(save.Mail!.Sends);
        Assert.Equal((f.Id, MailKind.ArmorReturn, MailSubjects.ArmorReturn, ArmorTierSweep.MailKey(key), 0L),
            (send.Draft.ToCharacterId, send.Draft.Kind, send.Draft.SubjectId, send.Draft.OpKey, send.Draft.Gold));
        Assert.Equal(new[] { (IronHelm, ItemInstanceIds.Make(1, 1)), (IronCuirass, ItemInstanceIds.Make(1, 2)) },
            send.Draft.Items.Select(i => (i.DefId, i.InstanceId)));
        Assert.All(send.Draft.Items, i => Assert.Contains("originalOwner", i.PropertiesJson));
        Assert.Equal(new[] { ArmorTierSweep.MailOutKey(key, Head), ArmorTierSweep.MailOutKey(key, Chest) },
            save.Inventory!.Ops.Select(o => o.OpKey));
        Assert.True(f.At(Equipped, Head).IsEmpty && f.At(Equipped, Chest).IsEmpty);

        f.Harness.Saves.CompleteAll();
        f.Harness.Ticks(2);

        Assert.Equal(SkillTreeReason.None, f.Transport.SkillResultsFor(1).Single().Reason);
        // A move to the mail, never a sink: ItemTransferred to no character.
        Assert.Equal(2, f.Transferred.Count(t => t.FromCharacterId == f.Id && t.ToCharacterId == 0));
    }

    [Fact]
    public void A_double_submit_takes_the_pieces_off_once_and_sends_one_mail()
    {
        var f0 = new Fixture("warrior", 1);
        var f = new Fixture("knight", 35, null, [Gear(1, Equipped, Chest, IronCuirass), .. FullSafeBag(f0.Items, 10)]);
        f.Harness.Saves.AutoComplete = false;

        f.Harness.SendSkillTree(1, 1, SkillTreeOp.Learn, CombatTraining);
        f.Harness.SendSkillTree(1, 1, SkillTreeOp.Learn, CombatTraining); // the same request id: the same op key
        f.Harness.Ticks(2);
        f.Harness.Saves.CompleteAll();
        f.Harness.Ticks(2);
        f.Harness.SendSkillTree(1, 1, SkillTreeOp.Learn, CombatTraining); // after the commit: the first answer
        f.Harness.Ticks(4);

        Assert.Single(f.Harness.Saves.Jobs.Where(j => j.Mail is { Sends.Count: > 0 }).SelectMany(j => j.Mail!.Sends));
        Assert.All(f.Transport.SkillResultsFor(1), r => Assert.Equal(SkillTreeReason.None, r.Reason));
        Assert.Single(f.Harness.Saves.Jobs.Where(j => j.Skills is { Ops.Count: > 0 }));
    }

    [Fact]
    public void Without_mail_a_full_bag_refuses_the_tier_change_and_no_piece_leaves()
    {
        // Review of #237: the pieces that do not fit leave only with a mail that can carry them; a world without mail refuses.
        var f0 = new Fixture("warrior", 1);
        var f = new Fixture("knight", 35, null, withMail: false, [Gear(1, Equipped, Chest, IronCuirass), .. FullSafeBag(f0.Items, 10)]);

        Assert.Equal(SkillTreeReason.Unavailable, f.Ask(SkillTreeOp.Learn, CombatTraining).Reason);

        Assert.Equal(ItemInstanceIds.Make(1, 1), f.At(Equipped, Chest).InstanceId);
        Assert.False(f.Harness.World.Skills!.Working(f.Id)!.IsLearned(CombatTraining));
        Assert.Equal(ArmorClass.Heavy, f.Inventory.ArmorTierOf(f.Id));
    }

    [Fact]
    public void A_respec_back_to_heavy_takes_the_medium_pieces_off()
    {
        // The Knight with Combat Training wears medium; a respec clears it and the class row's heavy applies again.
        var f = new Fixture("knight", 35, [CombatTraining], Gear(1, Equipped, Chest, LeatherVest), Gear(2, Equipped, Head, LeatherCap));
        Assert.Equal(ArmorClass.Medium, f.Inventory.ArmorTierOf(f.Id));

        Assert.Equal(SkillTreeReason.None, f.Ask(SkillTreeOp.Respec).Reason);

        Assert.Equal(ArmorClass.Heavy, f.Inventory.ArmorTierOf(f.Id));
        Assert.Equal(ArmorClass.Heavy, f.Parry);
        Assert.True(f.At(Equipped, Chest).IsEmpty && f.At(Equipped, Head).IsEmpty);
        Assert.Equal(new[] { ItemInstanceIds.Make(1, 2), ItemInstanceIds.Make(1, 1) }, new[] { f.At(Safe, 0).InstanceId, f.At(Safe, 1).InstanceId });
    }

    [Fact]
    public void Advancing_into_a_class_whose_row_sets_another_tier_takes_the_old_pieces_off()
    {
        // ClassDef: the archer is light, the thief medium (an advanced class whose row sets a tier, R-3.5-08).
        var f = new Fixture("archer", 30, null, Gear(1, Equipped, Chest, ClothShirt), Gear(2, Equipped, Legs, ClothPants));

        Assert.Equal(SkillTreeReason.None, f.Ask(SkillTreeOp.Advance, "thief").Reason);

        Assert.Equal(ArmorClass.Medium, f.Inventory.ArmorTierOf(f.Id));
        Assert.True(f.At(Equipped, Chest).IsEmpty && f.At(Equipped, Legs).IsEmpty);
    }

    [Fact]
    public void An_op_that_keeps_the_tier_touches_no_gear()
    {
        var f = new Fixture("warrior", 5, null, Gear(1, Equipped, Chest, IronCuirass));

        Assert.Equal(SkillTreeReason.None, f.Ask(SkillTreeOp.Learn, "warrior.piercingAttack").Reason);

        Assert.Equal(ItemInstanceIds.Make(1, 1), f.At(Equipped, Chest).InstanceId);
        Assert.DoesNotContain(f.Harness.Saves.Jobs, j => j.Inventory is { Ops.Count: > 0 });
    }

    [Fact]
    public void A_worn_piece_a_craft_holds_refuses_the_tier_change_and_nothing_is_applied()
    {
        var f = new Fixture("knight", 35, null, Gear(1, Equipped, Chest, IronCuirass));
        Assert.Equal(ReserveResult.Ok,
            f.Inventory.Reserve(f.Id, "enchant:test", [new ReserveItem(new InventoryLocation(Equipped, Chest), 1)]));
        int points = f.Harness.World.Progression!.Snapshot(f.Id).SkillPoints;

        Assert.Equal(SkillTreeReason.ArmorReserved, f.Ask(SkillTreeOp.Learn, CombatTraining).Reason);

        Assert.Equal(ItemInstanceIds.Make(1, 1), f.At(Equipped, Chest).InstanceId);
        Assert.Equal(points, f.Harness.World.Progression.Snapshot(f.Id).SkillPoints);
        Assert.False(f.Harness.World.Skills!.Working(f.Id)!.IsLearned(CombatTraining));
        Assert.Equal(ArmorClass.Heavy, f.Inventory.ArmorTierOf(f.Id));
    }

    [Fact]
    public void A_piece_already_worn_of_another_tier_stays_at_login()
    {
        // User answer 2026-11-13 (Q3): only putting a piece on is checked; nothing is swept at login.
        var f = new Fixture("warrior", 1, null, Gear(1, Equipped, Chest, ClothShirt));

        Assert.Equal(ItemInstanceIds.Make(1, 1), f.At(Equipped, Chest).InstanceId);
        Assert.Contains(f.Inventory.GetEquippedBonuses(f.Id), b => b.StatId == "def");
    }
}
