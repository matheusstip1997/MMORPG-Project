using GameServer.Entities;
using GameServer.Events;
using GameServer.Items;
using GameServer.Net;
using GameServer.Persistence;
using GameServer.Tests.Support;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Actions;
using Shared.Simulation.Items;

namespace GameServer.Tests.Items;

/// <summary>
/// Containers and equipment in the world tick by tick (WP-M2-1; spec 4.1 M2 item model, R-4.1-02, R-3.5-07): equip and
/// unequip through the inventory move request, the gear rules (class weapon list, minimum level, slot), the gear change rule
/// (out of combat and not during an action, <c>InCombat</c>), the starter weapon, equipment pickups into the Safe bag, and
/// what <see cref="IEquipmentStatSource"/> exposes once the save committed (rule 5).
/// </summary>
public class InventoryEquipWorldTests
{
    private const byte Normal = 1, Safe = 2, Valuables = 3, Quest = 4, Equipped = 5;
    private const byte Main = 0, Secondary = 1, Head = 2, Ring = 6;
    private const string Sword = "m2.trainingSword", Bow = "m2.trainingBow", Cap = "m2.leatherCap", RingItem = "m2.copperRing";

    private sealed class Fixture : IEventConsumer<EquipmentChanged>, IEventConsumer<ItemCreated>
    {
        public Fixture(string classId = "warrior", int level = 1, bool starterGranted = true, params ItemInstanceRow[] instances)
        {
            var tables = TestCombat.Tables();
            Items = ItemRules.From(tables)!;
            Bus = new EventBus(1, new FakeClock(), TestPipelineSettings.BusLimits());
            Bus.Subscribe<EquipmentChanged>(this);
            Bus.Subscribe<ItemCreated>(this);
            Harness = new WorldHarness(map: TestCombat.OpenMap(TestCombat.MonsterMarker(2_000, 0)),
                events: new EventDispatchHook(Bus), bus: Bus, combat: TestCombat.Rules(tables, TestCombat.Point()), items: Items);
            Character = Harness.Characters.Add(1, 0, new CharacterState(level, 0, "", 0, 0, 0, 0, 100, 50, 100), classId: classId);
            Harness.Characters.Instances[Character.CharacterId] = instances;
            if (!starterGranted)
                Harness.Characters.NoStarterYet.Add(Character.CharacterId);
            Player = Harness.Join(1);
            Harness.Ticks(1);
        }

        public WorldHarness Harness { get; }
        public ItemRules Items { get; }
        public EventBus Bus { get; }
        public CharacterRecord Character { get; }
        public Player Player { get; }
        public long Id => Character.CharacterId;
        public InventoryService Inventory => Harness.World.Inventory!;
        public FakeTransport Transport => Harness.Transport;
        public List<EquipmentChanged> Changed { get; } = [];
        public List<ItemCreated> Created { get; } = [];

        public void Handle(in EventEnvelope envelope, in EquipmentChanged payload) => Changed.Add(payload);

        public void Handle(in EventEnvelope envelope, in ItemCreated payload) => Created.Add(payload);

        public ItemStack At(byte container, int slot) => Inventory.CurrentInventory(Id).At(new InventoryLocation(container, slot));

        public ItemDef Item(string id)
        {
            Assert.True(Items.TryGetItem(id, out var item));
            return item;
        }

        /// <summary>The next class 2 batch (every second) and its completion.</summary>
        public void Save()
        {
            Harness.Clock.Advance(1_000);
            Harness.Ticks(2);
        }

        public InventoryMoveResult LastAnswer() => Transport.MoveResultsFor(1)[^1];
    }

    /// <summary>A placeholder gear instance created by the character with id 1 (its first ids).</summary>
    private static ItemInstanceRow Gear(long seq, byte container, int slot, string item, int rarity = 0) =>
        new(ItemInstanceIds.Make(1, seq), container, slot, item, rarity, false, false, ItemInstanceIds.NewProperties(1));

    [Fact]
    public void A_sword_from_the_safe_bag_is_equipped_and_its_scaled_bonuses_are_exposed_once_the_save_committed()
    {
        // The WP-M2-1 done criterion: the equipped set and its bonuses reach the stats through IEquipmentStatSource.
        var f = new Fixture(instances: Gear(1, Safe, 0, Sword, rarity: 1)); // uncommon: 110% of atk 10
        long sword = ItemInstanceIds.Make(1, 1);

        f.Harness.SendMove(1, requestId: 1, Safe, 0, Equipped, Main);
        f.Harness.Ticks(1);

        Assert.Equal(sword, f.At(Equipped, Main).InstanceId); // the world sees it at once
        Assert.True(f.At(Safe, 0).IsEmpty);
        Assert.Empty(f.Inventory.GetEquippedBonuses(f.Id)); // the stats see only what is committed (rule 5)
        Assert.Empty(f.Changed);

        f.Save();

        var bonus = Assert.Single(f.Inventory.GetEquippedBonuses(f.Id));
        Assert.Equal(("atk", 11, 0), (bonus.StatId, bonus.Flat, bonus.Percent));
        Assert.Equal(new[] { WeaponType.Sword }, f.Inventory.GetEquippedWeaponTypes(f.Id));
        Assert.Equal(new EquipmentChanged(f.Id, Main), Assert.Single(f.Changed));
        Assert.Equal((InventoryMoveReason.None, InventoryMoveOutcome.Moved), (f.LastAnswer().Reason, f.LastAnswer().Outcome));
        var equipped = Assert.Single(f.Transport.InventoryFor(1)[^1].Containers, c => c.Code == Equipped);
        var shown = Assert.Single(equipped.Slots);
        Assert.Equal((Main, (ushort)f.Item(Sword).NetId, sword, (ushort)1), (shown.Slot, shown.ItemNetId, shown.InstanceId, shown.Count));
        Assert.Empty(f.Created); // a move creates nothing
    }

    [Fact]
    public void Unequipping_takes_the_bonuses_away_after_the_save()
    {
        var f = new Fixture(instances: Gear(1, Equipped, Main, Sword));
        Assert.Single(f.Inventory.GetEquippedBonuses(f.Id)); // a login shows the saved equipped set

        f.Harness.SendMove(1, requestId: 1, Equipped, Main, Safe, 3);
        f.Harness.Ticks(1);
        f.Save();

        Assert.Equal(ItemInstanceIds.Make(1, 1), f.At(Safe, 3).InstanceId);
        Assert.Empty(f.Inventory.GetEquippedBonuses(f.Id));
        Assert.Empty(f.Inventory.GetEquippedWeaponTypes(f.Id));
        Assert.Equal(new EquipmentChanged(f.Id, Main), Assert.Single(f.Changed));
    }

    [Theory]
    [InlineData("warrior", Bow, Main, InventoryMoveReason.WeaponNotUsable)] // R-3.5-07: the class line's weapon list
    [InlineData("archer", Bow, Main, InventoryMoveReason.None)]
    [InlineData("warrior", RingItem, Ring, InventoryMoveReason.LevelTooLow)] // minLevel 5
    [InlineData("warrior", Cap, Main, InventoryMoveReason.ItemNotAccepted)] // a cap does not fill a weapon slot
    [InlineData("mage", Cap, Head, InventoryMoveReason.None)] // armor has no class gating
    public void Equipping_follows_the_gear_rules(string classId, string item, byte slot, InventoryMoveReason expected)
    {
        var f = new Fixture(classId, instances: Gear(1, Safe, 0, item));

        f.Harness.SendMove(1, requestId: 1, Safe, 0, Equipped, slot);
        f.Harness.Ticks(1);
        f.Save();

        Assert.Equal(expected, f.LastAnswer().Reason);
        Assert.Equal(expected == InventoryMoveReason.None, f.At(Safe, 0).IsEmpty);
    }

    [Fact]
    public void A_level_up_lets_the_ring_be_equipped()
    {
        var f = new Fixture(instances: Gear(1, Safe, 0, RingItem));
        f.Inventory.SetLevel(f.Id, 5); // what WP-M2-3's LevelUp hook calls

        f.Harness.SendMove(1, requestId: 1, Safe, 0, Equipped, Ring);
        f.Harness.Ticks(1);
        f.Save();

        Assert.Equal(InventoryMoveReason.None, f.LastAnswer().Reason);
        var bonus = Assert.Single(f.Inventory.GetEquippedBonuses(f.Id));
        Assert.Equal(("atk", 0, 3), (bonus.StatId, bonus.Flat, bonus.Percent));
    }

    [Fact]
    public void Gear_changes_are_refused_in_combat_but_bag_moves_are_not()
    {
        // Spec 4.1 M2 item model (user decision 2026-10-27): only out of combat (spec 12.7 combat state).
        var f = new Fixture(instances: [Gear(1, Safe, 0, Sword), Gear(2, Equipped, Head, Cap)]);
        f.Player.LastCombatAtMs = f.Harness.Clock.UtcNowMs;

        f.Harness.SendMove(1, requestId: 1, Safe, 0, Equipped, Main);
        f.Harness.SendMove(1, requestId: 2, Equipped, Head, Safe, 5);
        f.Harness.SendMove(1, requestId: 3, Safe, 0, Safe, 1);
        f.Harness.Ticks(1);

        var answers = f.Transport.MoveResultsFor(1);
        Assert.Equal(new[] { InventoryMoveReason.InCombat, InventoryMoveReason.InCombat }, answers.Select(a => a.Reason));
        Assert.Equal(ItemInstanceIds.Make(1, 1), f.At(Safe, 1).InstanceId); // moved inside the bag
        Assert.False(f.At(Equipped, Head).IsEmpty);

        // Once the combat state ran out, the same gear change goes through.
        f.Harness.Clock.Advance(f.Items.CombatStateMs);
        f.Harness.SendMove(1, requestId: 4, Safe, 1, Equipped, Main);
        f.Harness.Ticks(1);
        f.Save();
        Assert.Equal(InventoryMoveReason.None, f.LastAnswer().Reason);
    }

    [Fact]
    public void Gear_changes_are_refused_during_an_action_and_a_repeat_of_an_applied_change_keeps_its_answer()
    {
        var f = new Fixture(instances: Gear(1, Safe, 0, Sword));
        var handler = new InventoryMoveHandler(f.Inventory, "run", () => f.Harness.Clock.UtcNowMs);
        var equip = ClientRequest.Move(7, Safe, 0, Equipped, Main);

        f.Player.Actions.State = ActionState.Startup;
        handler.Handle(f.Player, equip);
        Assert.Equal(InventoryMoveReason.InCombat, Assert.Single(f.Player.MoveRefusals).Reason);
        Assert.True(f.At(Equipped, Main).IsEmpty);
        f.Player.MoveRefusals.Clear();

        f.Player.Actions.State = ActionState.Idle;
        handler.Handle(f.Player, equip);
        f.Player.Actions.State = ActionState.Startup;
        handler.Handle(f.Player, equip); // the same request again: the first answer, not a refusal (rule 5)

        Assert.Empty(f.Player.MoveRefusals);
        Assert.Equal(2, f.Player.PendingMoveConfirmations.Count);
        Assert.Equal(ItemInstanceIds.Make(1, 1), f.At(Equipped, Main).InstanceId);
        f.Player.Actions.State = ActionState.Idle;
    }

    [Fact]
    public void A_dual_weapon_fills_both_weapon_slots_and_reports_one_weapon_type()
    {
        var f = new Fixture("archer", instances: [Gear(1, Safe, 0, "m2.twinDaggers"), Gear(2, Equipped, Main, Bow)]);

        f.Harness.SendMove(1, requestId: 1, Safe, 0, Equipped, Secondary);
        f.Harness.Ticks(1);
        f.Save();

        Assert.Equal(InventoryMoveOutcome.Swapped, f.LastAnswer().Outcome);
        Assert.Equal(ItemInstanceIds.Make(1, 1), f.At(Equipped, Main).InstanceId);
        Assert.Equal(ItemInstanceIds.Make(1, 2), f.At(Safe, 0).InstanceId); // the bow went back where the daggers came from
        Assert.Equal(new[] { WeaponType.DualDaggers }, f.Inventory.GetEquippedWeaponTypes(f.Id));
    }

    [Fact]
    public void A_category_goes_only_where_it_is_accepted()
    {
        var f = new Fixture(instances: Gear(1, Safe, 0, Cap));
        Assert.Equal(3, f.Inventory.TryAdd(f.Id, f.Item("m1.demonHorn"), 0, 3, "add:1", InventoryReason.Admin, "test").Added);

        f.Harness.SendMove(1, requestId: 1, Normal, 0, Safe, 1); // a horn into the gear area
        f.Harness.SendMove(1, requestId: 2, Normal, 0, Valuables, 0); // into the valuables area: fine
        f.Harness.SendMove(1, requestId: 3, Safe, 0, Quest, 0); // a cap into the quest bag
        f.Harness.Ticks(1);
        f.Save();

        Assert.Equal(new[] { InventoryMoveReason.ItemNotAccepted, InventoryMoveReason.ItemNotAccepted, InventoryMoveReason.None },
            f.Transport.MoveResultsFor(1).Select(a => a.Reason));
        Assert.Equal(3, f.At(Valuables, 0).Count);
    }

    [Fact]
    public void Equipment_added_lands_in_the_safe_bag_as_new_instances_with_the_next_ids()
    {
        var f = new Fixture(instances: Gear(4, Safe, 0, Cap)); // the character's ids continue after the highest it holds

        var added = f.Inventory.TryAdd(f.Id, f.Item(Sword), 2, 1, "loot:1", InventoryReason.LootPickup, "drop");

        Assert.Equal(1, added.Added);
        var sword = f.At(Safe, 1);
        Assert.Equal((ItemInstanceIds.Make(1, 5), (byte)2, 1), (sword.InstanceId, sword.Rarity, sword.Count));
        var batch = f.Inventory.TakeBatch(f.Id, Guid.NewGuid())!;
        Assert.Equal(5, batch.InstanceSeq);
        var state = Assert.Single(batch.Instances!);
        Assert.Equal((Safe, 1, Sword, false), (state.Container, state.Slot, state.DefId, state.Removed));
        Assert.Contains("\"enchant\":0", state.PropertiesJson);
    }

    [Fact]
    public void The_starter_weapon_is_equipped_at_the_first_entry_and_granted_once()
    {
        var f = new Fixture(starterGranted: false);

        Assert.Equal((ushort)f.Item(Sword).NetId, f.At(Equipped, Main).ItemNetId);
        Assert.False(f.Inventory.GrantStarterGear(f.Id)); // its key is applied
        f.Save();

        Assert.Equal(new[] { WeaponType.Sword }, f.Inventory.GetEquippedWeaponTypes(f.Id));
        var created = Assert.Single(f.Created);
        Assert.Equal(((ushort)f.Item(Sword).NetId, 1, InventoryReason.StarterGear), (created.ItemNetId, created.Count, created.Reason));
        Assert.Equal(new EquipmentChanged(f.Id, Main), Assert.Single(f.Changed));
    }

    [Fact]
    public void A_repeated_equip_request_changes_nothing_and_gets_the_same_answer()
    {
        var f = new Fixture(instances: Gear(1, Safe, 0, Sword));

        f.Harness.SendMove(1, requestId: 1, Safe, 0, Equipped, Main);
        f.Harness.SendMove(1, requestId: 1, Safe, 0, Equipped, Main);
        f.Harness.Ticks(1);
        f.Save();

        var answers = f.Transport.MoveResultsFor(1);
        Assert.Equal(2, answers.Count);
        Assert.All(answers, a => Assert.Equal((InventoryMoveReason.None, InventoryMoveOutcome.Moved), (a.Reason, a.Outcome)));
        Assert.Single(f.Changed);
    }
}
