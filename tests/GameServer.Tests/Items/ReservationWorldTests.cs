using GameServer.Events;
using GameServer.Items;
using GameServer.Persistence;
using GameServer.Tests.Economy;
using GameServer.Tests.Support;
using Shared.Protocol;
using Shared.Simulation.Items;

namespace GameServer.Tests.Items;

/// <summary>
/// Reservations and item model v3 in the world tick by tick (spec 4.1, R-5.3-03, R-2.1-10, 9.13; WP-M3-2): a move request
/// of reserved units is answered <c>ItemReserved</c>, a sale of them too, the client sees the reserved flag and an
/// instance's typed properties (protocol 27), the session's end releases every reservation, and a vendor tool lands in the
/// Safe bag equipment area.
/// </summary>
public class ReservationWorldTests
{
    private const byte Normal = 1, Safe = 2;
    private const string Ore = "m3.oreT1";

    private static EconomyFixture WithOre(int count = 3, long gold = 0) =>
        new(gold: gold, bag: [EconomyFixture.Row(0, Ore, count), EconomyFixture.Row(1, EconomyFixture.Horn, 10)]);

    [Fact]
    public void A_move_request_of_reserved_ore_is_refused_and_allowed_once_released()
    {
        var f = WithOre();
        Assert.Equal(ReserveResult.Ok, f.Inventory.Reserve(f.CharacterId, "forge:1", [new ReserveItem(new InventoryLocation(Normal, 0), 3)]));

        f.Harness.SendMove(1, 1, Normal, 0, Normal, 7);
        f.Harness.Ticks(1);
        var refused = f.Transport.MoveResultsFor(1)[^1];
        Assert.Equal((1u, InventoryMoveReason.ItemReserved), (refused.RequestId, refused.Reason));

        f.Inventory.Release(f.CharacterId, "forge:1");
        f.Harness.SendMove(1, 2, Normal, 0, Normal, 7);
        f.Harness.Ticks(1);
        f.Save();
        var moved = f.Transport.MoveResultsFor(1)[^1];
        Assert.Equal((2u, InventoryMoveReason.None, InventoryMoveOutcome.Moved), (moved.RequestId, moved.Reason, moved.Outcome));
    }

    [Fact]
    public void The_client_sees_the_reserved_flag_without_a_save()
    {
        var f = WithOre();
        int before = f.Transport.InventoryFor(1).Count;

        f.Inventory.Reserve(f.CharacterId, "forge:1", [new ReserveItem(new InventoryLocation(Normal, 0), 2)]);
        f.Harness.Ticks(1);

        var contents = f.Transport.InventoryFor(1);
        Assert.True(contents.Count > before);
        Assert.True(contents[^1].Slots.Single(s => s.Slot == 0).Reserved);
        Assert.False(contents[^1].Slots.Single(s => s.Slot == 1).Reserved);

        f.Inventory.Release(f.CharacterId, "forge:1");
        f.Harness.Ticks(1);
        Assert.False(f.Transport.InventoryFor(1)[^1].Slots.Single(s => s.Slot == 0).Reserved);
    }

    [Fact]
    public void A_sale_of_reserved_units_is_refused_with_ItemReserved()
    {
        var f = WithOre();
        f.Inventory.Reserve(f.CharacterId, "forge:1", [new ReserveItem(new InventoryLocation(Normal, 1), 4)]); // 4 of 10 horns

        f.Harness.SendSell(1, 1, f.NpcId(EconomyFixture.Outpost), 1, 7, 70);
        f.Harness.Ticks(1);

        Assert.Equal(OutpostReason.ItemReserved, Assert.Single(f.Transport.SellResultsFor(1)).Reason);
        Assert.Equal(10, f.Inventory.Current(f.CharacterId)[1].Count);
    }

    [Fact]
    public void The_session_ending_releases_every_reservation()
    {
        // R-2.1-10: a lost session never leaves units locked (the character lingers, R-2.1-08; nothing was consumed).
        var tables = TestCombat.Tables();
        var harness = new WorldHarness(map: TestCombat.OpenMap(TestCombat.MonsterMarker(2_000, 0)),
            combat: TestCombat.Rules(tables, TestCombat.Point()), items: ItemRules.From(tables)!,
            values: TestSessions.Values(lingerOutOfCombatMs: 10_000));
        var character = harness.Characters.Add(1, 0, new CharacterState(1, 0, "", 0, 0, 0, 0, 100, 50, 100));
        harness.Characters.Items[character.CharacterId] = [EconomyFixture.Row(0, Ore, 3)];
        harness.Join(1);
        harness.Ticks(1);
        var inventory = harness.World.Inventory!;
        Assert.Equal(ReserveResult.Ok, inventory.Reserve(character.CharacterId, "forge:1", [new ReserveItem(new InventoryLocation(Normal, 0), 3)]));

        harness.Leave(1);
        harness.Ticks(1);

        Assert.True(harness.Sessions.IsLingering(character.CharacterId));
        Assert.True(inventory.IsAttached(character.CharacterId));
        Assert.False(inventory.HasReservation(character.CharacterId, "forge:1"));
        Assert.Equal(3, inventory.Unreserved(character.CharacterId, new InventoryLocation(Normal, 0)));
    }

    [Fact]
    public void A_bought_pickaxe_lands_in_the_safe_bag_equipment_area_as_an_instance()
    {
        // R-4.2-01 (WP-M3-2): vendors sell basic tools; the price 50 is a placeholder (NpcVendorStock).
        var f = new EconomyFixture(gold: 100);
        ushort pickaxe = f.NetId("m3.pickaxeT1");

        f.Harness.SendBuy(1, 1, f.NpcId(EconomyFixture.Vendor), pickaxe, 1, 50);
        f.Harness.Ticks(1);
        Assert.Empty(f.Transport.BuyResultsFor(1)); // not before the commit
        f.Commit();

        var result = Assert.Single(f.Transport.BuyResultsFor(1));
        Assert.Equal((OutpostReason.None, pickaxe, (ushort)1, 50L), (result.Reason, result.ItemNetId, result.Count, result.GoldSpent));
        Assert.Equal(50, f.Inventory.CommittedGold(f.CharacterId));
        var stack = f.Inventory.CurrentInventory(f.CharacterId).At(new InventoryLocation(Safe, 0));
        Assert.Equal((pickaxe, 1, true), (stack.ItemNetId, stack.Count, stack.IsInstance));
        Assert.True(f.Inventory.Current(f.CharacterId)[0].IsEmpty); // not the Normal bag
        var safe = f.Transport.InventoryFor(1)[^1].Containers.Single(c => c.Code == Safe);
        Assert.Equal(stack.InstanceId, Assert.Single(safe.Slots).InstanceId);
        Assert.True(f.Inventory.Rules.TryGetTool(pickaxe, out _));
    }

    [Theory]
    [InlineData(2)] // a second pickaxe: one instance per purchase (maxStack 1)
    [InlineData(0)]
    public void A_tool_is_bought_one_at_a_time(ushort count)
    {
        var f = new EconomyFixture(gold: 1_000);

        f.Harness.SendBuy(1, 1, f.NpcId(EconomyFixture.Vendor), f.NetId("m3.pickaxeT1"), count, count * 50);
        f.Harness.Ticks(1);

        Assert.Equal(OutpostReason.NotInStock, Assert.Single(f.Transport.BuyResultsFor(1)).Reason);
    }

    [Fact]
    public void A_full_safe_bag_refuses_a_mold_with_BagFull()
    {
        var f = new EconomyFixture(gold: 1_000);
        for (int i = 0; i < f.Inventory.Rules.Containers.Single(c => c.Code == Safe).Slots; i++)
            Assert.Equal(1, f.Inventory.TryAdd(f.CharacterId, Item(f, "m3.moldRing"), 0, 1, $"seed:{i}", InventoryReason.Admin, "test").Added);

        f.Harness.SendBuy(1, 1, f.NpcId(EconomyFixture.Vendor), f.NetId("m3.moldSword"), 1, 40);
        f.Harness.Ticks(1);

        Assert.Equal(OutpostReason.BagFull, Assert.Single(f.Transport.BuyResultsFor(1)).Reason);
        Assert.Equal(1_000, f.Inventory.Gold(f.CharacterId));
    }

    [Fact]
    public void A_crafted_instance_reaches_the_client_with_its_typed_properties()
    {
        var f = new EconomyFixture();
        var crafted = ItemProperties.New(f.CharacterId);
        crafted.CraftedStats.Add(new ItemStatBonus("atk", 21, 0));
        crafted.CraftedStats.Add(new ItemStatBonus("def", 0, 4));
        crafted.Quality = 730;
        crafted.CrafterId = f.CharacterId;
        crafted.Tainted = true;
        crafted.Enchant = 2;
        f.Inventory.TryAdd(f.CharacterId, Item(f, "m2.trainingSword"), 1, 1, "forge:1:output", InventoryReason.Admin, "test", crafted);

        f.Save();

        var slot = Assert.Single(f.Transport.InventoryFor(1)[^1].Containers.Single(c => c.Code == Safe).Slots);
        Assert.Equal(((ushort)730, f.CharacterId, true, (byte)2, false), (slot.Quality, slot.CrafterId, slot.Tainted, slot.Enchant, slot.Reserved));
        int atk = f.Inventory.Rules.StatIndex("atk"), def = f.Inventory.Rules.StatIndex("def");
        Assert.Equal(new[] { ((byte)atk, 21, 0), ((byte)def, 0, 4) }, slot.CraftedStats.Select(s => (s.Stat, s.Flat, s.Percent)));
        Assert.Empty(slot.Gems);
    }

    private static Shared.Data.Tables.ItemDef Item(EconomyFixture f, string id)
    {
        Assert.True(f.Inventory.Rules.TryGetItem(id, out var item));
        return item;
    }
}
