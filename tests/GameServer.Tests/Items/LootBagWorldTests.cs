using GameServer.Entities;
using GameServer.Events;
using GameServer.Items;
using GameServer.Items.LootBags;
using GameServer.Persistence;
using GameServer.Tests.Support;
using GameServer.World.Map;
using Shared.Protocol;
using Shared.World;

namespace GameServer.Tests.Items;

/// <summary>
/// Death loot bags in the world (WP-M4-9, spec 4.3 R-4.3-03): a death in the field (<c>deathDrop</c> on) drops the Normal bag as
/// a bag entity once the death's save committed, the owner is told of its own bag at any distance (<c>OwnLootBags</c>), an open
/// sends the contents, and a take through the router is answered after its save; a death in the city drops nothing.
/// </summary>
public class LootBagWorldTests
{
    private const string Horn = "m1.demonHorn";

    /// <summary>The city west of x = 0 (normalCity, no drops), the field east of it (openWorld, drops).</summary>
    private static WorldMap Map(int spawnXCm)
    {
        int cells = 2 * TestMaps.OpenHalfSizeCm / TestMaps.OpenCellCm;
        var grid = TestMaps.Grid(-TestMaps.OpenHalfSizeCm, -TestMaps.OpenHalfSizeCm, TestMaps.OpenCellCm, cells, cells, 0, (_, _) => true,
            region: (x, _) => x < cells / 2 ? (byte)2 : (byte)1);
        return TestMaps.Map(grid, [TestMaps.SpawnMarker(spawnXCm, 0)],
        [
            new WorldRegion(1, "test.field", "openWorld", new RegionFlags(PvpMode.ArmedOnly, true, DeathDrop.On)),
            new WorldRegion(2, "test.city", "normalCity", new RegionFlags(PvpMode.Off, true, DeathDrop.Off)),
        ]);
    }

    private sealed class Fixture
    {
        public Fixture(int spawnXCm)
        {
            var tables = TestCombat.Tables();
            Bus = new EventBus(1, new FakeClock(), TestPipelineSettings.BusLimits());
            var items = ItemRules.From(tables)!;
            Harness = new WorldHarness(map: Map(spawnXCm), events: new EventDispatchHook(Bus), bus: Bus, items: items,
                lootBags: LootBagConfig.From(tables, items.Settings));
            var character = Harness.Characters.Add(1, 0, new CharacterState(5, 0, "", spawnXCm, 0, 0, 0, 100, 50, 100));
            Harness.Characters.Items[character.CharacterId] = [new ItemStackRow(1, 0, Horn, 0, false, 6)];
            Player = Harness.Join(1);
            Harness.Ticks(1);
        }

        public WorldHarness Harness { get; }
        public EventBus Bus { get; }
        public Player Player { get; }
        public FakeTransport Transport => Harness.Transport;
        public List<LootBag> Bags() => Harness.World.Entities.OfType<LootBag>().ToList();

        /// <summary>A death as HitResolver (or the dev console's /die) publishes it, then its save and a few ticks.</summary>
        public void Die()
        {
            Bus.Publish(new Died(Player.Id, 0), Player.CharacterId);
            Harness.Ticks(4);
        }
    }

    [Fact]
    public void A_death_in_the_field_drops_a_bag_its_owner_sees_and_takes_back()
    {
        var f = new Fixture(spawnXCm: 500);
        Assert.Empty(Assert.Single(f.Transport.OwnLootBagsFor(1)).Bags); // at enter world: none

        f.Die();

        var bag = Assert.Single(f.Bags());
        var own = Assert.Single(f.Transport.OwnLootBagsFor(1)[^1].Bags);
        Assert.Equal((bag.Id, 6), (own.BagEntityId, (int)own.Units));
        Assert.True(f.Harness.World.Inventory!.Current(f.Player.CharacterId)[0].IsEmpty);

        f.Harness.SendLootBag(1, 1, bag.Id, LootBagOp.Open);
        f.Harness.Ticks(1);
        var content = f.Transport.LootBagContentsFor(1)[^1];
        Assert.Equal((LootBagAccess.Own, 6), (content.Access, (int)content.Rows.Single().Count));

        f.Harness.SendLootBag(1, 2, bag.Id, LootBagOp.TakeAll);
        f.Harness.Ticks(4);

        var answer = f.Transport.LootBagResultsFor(1).Single(r => r.RequestId == 2);
        Assert.Equal((LootBagReason.None, (ushort)6), (answer.Reason, answer.Taken));
        Assert.Empty(f.Bags());
        Assert.Equal(6, f.Harness.World.Inventory!.Current(f.Player.CharacterId)[0].Count);
        Assert.Empty(f.Transport.OwnLootBagsFor(1)[^1].Bags);
        Assert.True(f.Transport.LootBagContentsFor(1)[^1].Closed); // the open window closes with the bag
    }

    [Fact]
    public void A_death_in_the_city_drops_nothing()
    {
        var f = new Fixture(spawnXCm: -500);

        f.Die();

        Assert.Empty(f.Bags());
        Assert.Equal(6, f.Harness.World.Inventory!.Current(f.Player.CharacterId)[0].Count);
    }

    [Fact]
    public void A_bag_request_without_a_bag_is_refused_with_its_reason()
    {
        var f = new Fixture(spawnXCm: 500);

        f.Harness.SendLootBag(1, 9, 424_242, LootBagOp.TakeAll);
        f.Harness.Ticks(1);

        Assert.Equal(LootBagReason.BagNotFound, Assert.Single(f.Transport.LootBagResultsFor(1)).Reason);
    }
}
