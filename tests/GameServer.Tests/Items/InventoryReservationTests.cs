using GameServer.Events;
using GameServer.Items;
using GameServer.Persistence;
using GameServer.Tests.Support;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Items;

namespace GameServer.Tests.Items;

/// <summary>
/// Item model v3 in <see cref="InventoryService"/> (spec 4.1 M3 item model and reservations, R-5.3-03, R-2.1-10; WP-M3-2):
/// reservations refuse every path that would move, split, merge, swap, equip, unequip, displace or remove reserved units;
/// release and consume; two reservations cannot take the same units; the typed properties (parsed at login, written as v2,
/// a crafted stats snapshot instead of the item's list) and the taint rule.
/// </summary>
public class InventoryReservationTests
{
    private const long Id = 1;
    private const byte Normal = 1, Safe = 2, Equipped = 5;
    private const string Ore = "m3.oreT1", Ingot = "m3.ingotT1", Sword = "m2.trainingSword";

    private static readonly ItemRules Items = ItemRules.From(TestCombat.Tables())!;

    private static InventoryLocation At(byte container, int slot) => new(container, slot);

    private static ItemStackRow Stack(int slot, string item, int count, bool stolen = false) => new(Normal, slot, item, 0, stolen, count);

    private static ItemInstanceRow Gear(long seq, byte container, int slot, string item = Sword, int rarity = 0, string? properties = null) =>
        new(ItemInstanceIds.Make(Id, seq), container, slot, item, rarity, false, false, properties ?? ItemInstanceIds.NewProperties(Id));

    private static InventoryService Attach(ItemStackRow[]? stacks = null, ItemInstanceRow[]? instances = null, EventBus? events = null)
    {
        var service = new InventoryService(Items, events);
        Assert.True(service.Attach(Id, new InventoryLoad(stacks ?? [], instances ?? [], 10, true), "warrior", 20));
        return service;
    }

    private static ItemDef Item(string id)
    {
        Assert.True(Items.TryGetItem(id, out var item));
        return item;
    }

    private static ReserveItem[] Units(InventoryLocation at, int count) => [new ReserveItem(at, count)];

    [Fact]
    public void Three_reserved_ore_refuse_a_move_and_move_once_released()
    {
        // The WP-M3-2 done check: a test reserves three ore, a move of them is refused, and releasing them allows it.
        var inventory = Attach([Stack(0, Ore, 3)]);
        Assert.Equal(ReserveResult.Ok, inventory.Reserve(Id, "forge:1", Units(At(Normal, 0), 3)));

        var refused = inventory.TryMove(Id, At(Normal, 0), At(Normal, 5), "move:1", InventoryReason.PlayerMove);
        Assert.Equal((BagMoveResult.Refused, InventoryMoveReason.ItemReserved), (refused.Result, refused.Reason));
        Assert.False(inventory.WasApplied(Id, "move:1"));

        Assert.True(inventory.Release(Id, "forge:1"));
        Assert.True(inventory.TryMove(Id, At(Normal, 0), At(Normal, 5), "move:2", InventoryReason.PlayerMove).Applied);
        Assert.Equal(3, inventory.CurrentInventory(Id).At(At(Normal, 5)).Count);
    }

    [Fact]
    public void A_reserved_stack_refuses_every_move_kind()
    {
        var inventory = Attach([Stack(0, Ore, 5), Stack(1, Ore, 2), Stack(2, Ingot, 4)]);
        Assert.Equal(ReserveResult.Ok, inventory.Reserve(Id, "forge:1", Units(At(Normal, 0), 1)));

        InventoryMoveReason Reason(InventoryOpResult result) => result.Applied ? InventoryMoveReason.None : result.Reason;
        Assert.Equal(InventoryMoveReason.ItemReserved, Reason(inventory.TryMove(Id, At(Normal, 0), At(Normal, 9), "a", InventoryReason.PlayerMove))); // move
        Assert.Equal(InventoryMoveReason.ItemReserved, Reason(inventory.TryMove(Id, At(Normal, 0), At(Normal, 9), 2, "b", InventoryReason.PlayerMove))); // split
        Assert.Equal(InventoryMoveReason.ItemReserved, Reason(inventory.TryMove(Id, At(Normal, 0), At(Normal, 1), "c", InventoryReason.PlayerMove))); // merge away
        Assert.Equal(InventoryMoveReason.ItemReserved, Reason(inventory.TryMove(Id, At(Normal, 1), At(Normal, 0), "d", InventoryReason.PlayerMove))); // merge onto
        Assert.Equal(InventoryMoveReason.ItemReserved, Reason(inventory.TryMove(Id, At(Normal, 2), At(Normal, 0), "e", InventoryReason.PlayerMove))); // swap
        Assert.Equal(InventoryMoveReason.ItemReserved, Reason(inventory.TryMove(Id, At(Normal, 0), At(3, 0), "f", InventoryReason.PlayerMove))); // to the valuables area
        Assert.Equal(BagMoveResult.Refused, inventory.TryMove(Id, 0, 9, "g", InventoryReason.PlayerMove)); // the M1 bag move
        // The unreserved stacks still move.
        Assert.True(inventory.TryMove(Id, At(Normal, 1), At(Normal, 9), "h", InventoryReason.PlayerMove).Applied);
        Assert.Equal(5, inventory.CurrentInventory(Id).At(At(Normal, 0)).Count);
    }

    [Fact]
    public void A_reserved_equipped_item_cannot_be_unequipped_or_displaced_and_a_reserved_one_cannot_be_equipped()
    {
        var inventory = Attach(instances: [Gear(1, Equipped, 0), Gear(2, Safe, 0), Gear(3, Safe, 1)]);
        Assert.Equal(ReserveResult.Ok, inventory.Reserve(Id, "enchant:1", Units(At(Equipped, 0), 1)));

        Assert.Equal(InventoryMoveReason.ItemReserved, inventory.TryUnequip(Id, 0, At(Safe, 5), "a").Reason);
        Assert.Equal(InventoryMoveReason.ItemReserved, inventory.TryEquip(Id, At(Safe, 0), 0, "b").Reason); // would displace it
        Assert.Equal(InventoryMoveReason.ItemReserved, inventory.TryUnequip(Id, 0, At(Safe, 1), "c").Reason); // unequip onto gear

        Assert.True(inventory.Release(Id, "enchant:1"));
        Assert.Equal(ReserveResult.Ok, inventory.Reserve(Id, "enchant:2", Units(At(Safe, 0), 1)));
        Assert.Equal(InventoryMoveReason.ItemReserved, inventory.TryEquip(Id, At(Safe, 0), 0, "d").Reason);
        Assert.True(inventory.TryEquip(Id, At(Safe, 1), 0, "e").Applied); // another sword displaces the unreserved one
    }

    [Fact]
    public void A_remove_takes_only_unreserved_units()
    {
        var inventory = Attach([Stack(0, Ore, 5)]);
        Assert.Equal(ReserveResult.Ok, inventory.Reserve(Id, "forge:1", Units(At(Normal, 0), 3)));

        Assert.Equal(2, inventory.Unreserved(Id, At(Normal, 0)));
        Assert.True(inventory.IsReserved(Id, At(Normal, 0)));
        Assert.False(inventory.TryRemove(Id, Normal, 0, 3, "use:1", InventoryReason.Admin, "test"));
        Assert.True(inventory.TryRemove(Id, Normal, 0, 2, "use:2", InventoryReason.Admin, "test"));
        Assert.False(inventory.TryRemove(Id, Normal, 0, 1, "use:3", InventoryReason.Admin, "test"));
        Assert.Equal(3, inventory.CurrentInventory(Id).At(At(Normal, 0)).Count);
        Assert.Equal(0, inventory.Unreserved(Id, At(Normal, 0)));
    }

    [Fact]
    public void A_pickup_may_still_merge_onto_a_reserved_stack()
    {
        var inventory = Attach([Stack(0, Ore, 3)]);
        Assert.Equal(ReserveResult.Ok, inventory.Reserve(Id, "forge:1", Units(At(Normal, 0), 3)));

        Assert.Equal(2, inventory.TryAdd(Id, Item(Ore), 0, 2, "pickup:1", InventoryReason.LootPickup, "test").Added);

        Assert.Equal((5, 2), (inventory.CurrentInventory(Id).At(At(Normal, 0)).Count, inventory.Unreserved(Id, At(Normal, 0))));
    }

    [Fact]
    public void Two_reservations_cannot_take_the_same_units()
    {
        var inventory = Attach([Stack(0, Ore, 5)]);

        Assert.Equal(ReserveResult.Ok, inventory.Reserve(Id, "a", Units(At(Normal, 0), 3)));
        Assert.Equal(ReserveResult.ItemReserved, inventory.Reserve(Id, "b", Units(At(Normal, 0), 3)));
        Assert.False(inventory.HasReservation(Id, "b"));
        Assert.Equal(ReserveResult.Ok, inventory.Reserve(Id, "b", Units(At(Normal, 0), 2)));
        Assert.Equal(0, inventory.Unreserved(Id, At(Normal, 0)));
        Assert.True(inventory.Release(Id, "a"));
        Assert.Equal(3, inventory.Unreserved(Id, At(Normal, 0)));
    }

    [Fact]
    public void A_reservation_is_all_or_nothing_and_checks_its_key_and_units()
    {
        var inventory = Attach([Stack(0, Ore, 2), Stack(1, Ingot, 1)]);

        Assert.Equal(ReserveResult.NotEnough, inventory.Reserve(Id, "a", [new ReserveItem(At(Normal, 1), 1), new ReserveItem(At(Normal, 0), 3)]));
        Assert.Equal(ReserveResult.NotEnough, inventory.Reserve(Id, "a", Units(At(Normal, 7), 1))); // empty
        Assert.Equal(ReserveResult.NotEnough, inventory.Reserve(Id, "a", Units(At(9, 0), 1))); // no such container
        Assert.False(inventory.IsReserved(Id, At(Normal, 1))); // nothing of the refused request stayed
        // The same slot twice adds up.
        Assert.Equal(ReserveResult.NotEnough, inventory.Reserve(Id, "a", [new ReserveItem(At(Normal, 0), 2), new ReserveItem(At(Normal, 0), 1)]));
        Assert.Equal(ReserveResult.Ok, inventory.Reserve(Id, "a", [new ReserveItem(At(Normal, 0), 1), new ReserveItem(At(Normal, 1), 1)]));
        Assert.Equal(ReserveResult.KeyInUse, inventory.Reserve(Id, "a", Units(At(Normal, 0), 1)));
        Assert.False(inventory.Release(Id, "unknown"));
    }

    [Fact]
    public void Consuming_a_reservation_removes_its_units_once_and_ends_it()
    {
        var events = new EventBus(1, new FakeClock(), TestPipelineSettings.BusLimits());
        var destroyed = new Recorder();
        events.Subscribe<ItemDestroyed>(destroyed);
        var inventory = Attach([Stack(0, Ore, 5), Stack(1, Ingot, 2)], events: events);
        Assert.Equal(ReserveResult.Ok, inventory.Reserve(Id, "forge:1", [new ReserveItem(At(Normal, 0), 3), new ReserveItem(At(Normal, 1), 2)]));

        Assert.True(inventory.TryRemoveReserved(Id, "forge:1", "forge:1:inputs", InventoryReason.Admin, "forge"));
        Assert.True(inventory.TryRemoveReserved(Id, "forge:1", "forge:1:inputs", InventoryReason.Admin, "forge")); // repeated key

        var bag = inventory.CurrentInventory(Id);
        Assert.Equal((2, true), (bag.At(At(Normal, 0)).Count, bag.At(At(Normal, 1)).IsEmpty));
        Assert.False(inventory.HasReservation(Id, "forge:1"));
        Assert.False(inventory.IsReserved(Id, At(Normal, 0)));
        Assert.False(inventory.TryRemoveReserved(Id, "forge:1", "forge:1:again", InventoryReason.Admin, "forge"));

        // One operation, two items: one ItemDestroyed per item after the commit.
        var saveId = Guid.NewGuid();
        var batch = inventory.TakeBatch(Id, saveId)!;
        Assert.Single(batch.Ops);
        inventory.Committed(Id, saveId);
        events.DispatchQueued();
        Assert.Equal(new[] { ((ushort)Item(Ore).NetId, 3), ((ushort)Item(Ingot).NetId, 2) },
            destroyed.Items.Select(e => (e.ItemNetId, e.Count)));
    }

    [Fact]
    public void Release_all_ends_every_reservation_of_the_character()
    {
        var changed = new List<long>();
        var inventory = Attach([Stack(0, Ore, 5)]);
        inventory.ReservationsChanged = changed.Add;
        inventory.Reserve(Id, "a", Units(At(Normal, 0), 1));
        inventory.Reserve(Id, "b", Units(At(Normal, 0), 1));

        inventory.ReleaseAll(Id);

        Assert.False(inventory.HasReservation(Id, "a") || inventory.HasReservation(Id, "b"));
        Assert.Equal(5, inventory.Unreserved(Id, At(Normal, 0)));
        Assert.Equal(new long[] { Id, Id, Id }, changed);
    }

    [Fact]
    public void A_stolen_or_tainted_input_taints_the_output()
    {
        // R-4.4-03, the "any" rule.
        Assert.False(ItemRules.TaintsOutput([(false, false), (false, false)]));
        Assert.True(ItemRules.TaintsOutput([(false, false), (true, false)]));
        Assert.True(ItemRules.TaintsOutput([(false, true)]));

        var tainted = ItemProperties.New(Id);
        tainted.Tainted = true;
        var inventory = Attach([Stack(0, Ore, 2), Stack(1, Ore, 2, stolen: true)], [Gear(1, Safe, 0, properties: tainted.ToJson()), Gear(2, Safe, 1)]);
        inventory.Reserve(Id, "clean", Units(At(Normal, 0), 1));
        inventory.Reserve(Id, "stolen", Units(At(Normal, 1), 1));
        inventory.Reserve(Id, "taintedGear", Units(At(Safe, 0), 1));
        inventory.Reserve(Id, "cleanGear", Units(At(Safe, 1), 1));

        Assert.False(inventory.TaintsOutput(Id, "clean"));
        Assert.True(inventory.TaintsOutput(Id, "stolen"));
        Assert.True(inventory.TaintsOutput(Id, "taintedGear"));
        Assert.False(inventory.TaintsOutput(Id, "cleanGear"));
    }

    [Fact]
    public void Equipped_gear_gives_its_crafted_snapshot_and_other_gear_its_item_list_at_its_rarity()
    {
        var crafted = ItemProperties.New(Id);
        crafted.CraftedStats.Add(new ItemStatBonus("atk", 37, 0));
        crafted.CraftedStats.Add(new ItemStatBonus("str", 2, 0));
        crafted.Quality = 500;
        crafted.CrafterId = Id;
        var withSnapshot = Attach(instances: [Gear(1, Equipped, 0, rarity: 2, properties: crafted.ToJson())]);
        var dropped = Attach(instances: [Gear(1, Equipped, 0, rarity: 2)]);

        // The snapshot as stored: not scaled by rarity again (it already holds it, spec 5.3).
        Assert.Equal(new[] { ("atk", 37, 0), ("str", 2, 0) }, withSnapshot.GetEquippedBonuses(Id).Select(b => (b.StatId, b.Flat, b.Percent)));
        // Dropped gear: m2.trainingSword's ItemStatBonusDef row (atk +10, placeholder) at rare's statPercent (125, placeholder).
        var expected = Items.BaseBonuses(Item(Sword).NetId).Select(b => b.Scale(Items.StatPercent(2)));
        Assert.Equal(expected.Select(b => (b.StatId, b.Flat, b.Percent)), dropped.GetEquippedBonuses(Id).Select(b => (b.StatId, b.Flat, b.Percent)));
        Assert.NotEmpty(dropped.GetEquippedBonuses(Id));
    }

    [Theory]
    [InlineData("{\"v\":3}")] // a later version
    [InlineData("{\"v\":2,\"enchant\":\"x\"}")] // malformed
    [InlineData("{\"v\":2,\"craftedStats\":[{\"stat\":\"noSuchStat\",\"flat\":1}]}")] // a stat the tables do not know
    [InlineData("{\"v\":2,\"gems\":[\"m3.noSuchGem\"]}")] // a gem the tables do not know
    public void Properties_the_tables_cannot_represent_refuse_the_login(string properties)
    {
        var service = new InventoryService(Items, events: null);

        Assert.False(service.Attach(Id, new InventoryLoad([], [Gear(1, Safe, 0, properties: properties)], 1, true), "warrior", 1));
        Assert.False(service.IsAttached(Id));
    }

    [Fact]
    public void A_v1_instance_is_parsed_at_login_and_written_as_v2_when_touched()
    {
        const string v1 = "{\"v\":1,\"enchant\":0,\"gems\":[],\"originalOwner\":1,\"ownerHistory\":[],\"legacy\":true}";
        var inventory = Attach(instances: [Gear(1, Safe, 0, properties: v1)]);
        Assert.True(inventory.TryGetProperties(Id, ItemInstanceIds.Make(Id, 1), out var properties));
        Assert.Equal(1, properties.ReadVersion);

        Assert.True(inventory.TryMove(Id, At(Safe, 0), At(Safe, 3), "move:1", InventoryReason.PlayerMove).Applied);
        var state = Assert.Single(inventory.TakeBatch(Id, Guid.NewGuid())!.Instances!);

        Assert.StartsWith("{\"v\":2,", state.PropertiesJson);
        Assert.EndsWith(",\"legacy\":true}", state.PropertiesJson); // the unknown member is kept
    }

    [Fact]
    public void An_instance_created_with_properties_gets_its_own_copy_owned_by_the_character()
    {
        var inventory = Attach();
        var template = ItemProperties.New(originalOwner: 99);
        template.CraftedStats.Add(new ItemStatBonus("atk", 12, 0));
        template.Quality = 640;
        template.CrafterId = Id;

        Assert.Equal(1, inventory.TryAdd(Id, Item(Sword), 1, 1, "forge:1:output", InventoryReason.Admin, "forge", template).Added);
        template.CraftedStats.Clear();

        var stack = inventory.CurrentInventory(Id).At(At(Safe, 0));
        Assert.True(inventory.TryGetProperties(Id, stack.InstanceId, out var properties));
        Assert.Equal((12, 640, Id, Id), (properties.CraftedStats.Single().Flat, properties.Quality, properties.CrafterId, properties.OriginalOwner));
        var state = Assert.Single(inventory.TakeBatch(Id, Guid.NewGuid())!.Instances!);
        Assert.Contains("\"craftedStats\":[{\"stat\":\"atk\",\"flat\":12,\"percent\":0}]", state.PropertiesJson);
        Assert.Throws<ArgumentException>(() => inventory.TryAdd(Id, Item(Ore), 0, 1, "x", InventoryReason.Admin, "test", template));
    }

    [Fact]
    public void Tools_are_known_by_their_item()
    {
        Assert.True(Items.TryGetTool(Item("m3.pickaxeT1").NetId, out var tool));
        Assert.Equal((ToolType.Pickaxe, 1), (tool.ToolType, tool.Tier));
        Assert.False(Items.TryGetTool(Item("m3.moldSword").NetId, out _));
    }

    private sealed class Recorder : IEventConsumer<ItemDestroyed>
    {
        public List<ItemDestroyed> Items { get; } = [];

        public void Handle(in EventEnvelope envelope, in ItemDestroyed payload) => Items.Add(payload);
    }
}
