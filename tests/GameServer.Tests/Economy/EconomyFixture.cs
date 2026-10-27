using GameServer.Economy;
using GameServer.Entities;
using GameServer.Events;
using GameServer.Items;
using GameServer.Persistence;
using GameServer.Tests.Support;
using GameServer.World.Map;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Simulation;
using Shared.World;

namespace GameServer.Tests.Economy;

/// <summary>
/// A world with the repository's item and economy tables (WP-M2-4): the player spawn at the origin, an outpost NPC 2 m
/// east (in reach), a vendor NPC 2 m north, a second outpost far away, and one demon 20 m east. Players join with the
/// given bag and gold; the save worker completes at once unless told otherwise.
/// </summary>
internal sealed class EconomyFixture
{
    public const string Outpost = "test.npc.outpost";
    public const string Vendor = "test.npc.vendor";
    public const string FarOutpost = "test.npc.far";
    public const string Horn = "m1.demonHorn"; // sell-only, base 10 gold
    public const string Ash = "m1.demonAsh"; // a material
    public const string Potion = "m2.potionHpSmall"; // vendor price 25

    public EconomyFixture(long gold = 0, ItemStackRow[]? bag = null, Action<ConfigSet>? edit = null, int players = 1)
    {
        var tables = TestCombat.Tables();
        edit?.Invoke(tables);
        Tables = tables;
        Items = ItemRules.From(tables)!;
        Rules = EconomyRules.From(tables)!;
        Bus = new EventBus(1, new FakeClock(), TestPipelineSettings.BusLimits());
        Bus.Subscribe<OutpostVisited>(Events);
        Bus.Subscribe<ItemSold>(Events);
        Bus.Subscribe<GoldChanged>(Events);
        Bus.Subscribe<GoldDestroyed>(Events);
        var open = TestMaps.Open();
        var map = TestMaps.Map(open.Grid,
        [
            TestMaps.SpawnMarker(0, 0), TestCombat.MonsterMarker(2_000, 0), Npc(Outpost, 200, 0, "m1.outpost"),
            Npc(Vendor, 0, 200, "m1.vendor"), Npc(FarOutpost, 5_000, 5_000, "m1.outpost"),
        ]);
        Harness = new WorldHarness(map: map, events: new EventDispatchHook(Bus), bus: Bus,
            combat: TestCombat.Rules(tables, TestCombat.Point()), items: Items, economy: Rules);
        for (int i = 1; i <= players; i++)
        {
            var character = Harness.Characters.Add(i, 0, new CharacterState(1, 0, "", 0, 0, 0, 0, 100, 50, 100));
            Harness.Characters.Items[character.CharacterId] = bag ?? [];
            Harness.Characters.StoreGold(character.CharacterId, gold);
            Characters.Add(character);
            Players.Add(Harness.Join(i));
        }
        Harness.Ticks(1);
    }

    public ConfigSet Tables { get; }
    public WorldHarness Harness { get; }
    public EventBus Bus { get; }
    public ItemRules Items { get; }
    public EconomyRules Rules { get; }
    public EventRecorder Events { get; } = new();
    public List<CharacterRecord> Characters { get; } = [];
    public List<Player> Players { get; } = [];
    public Player Player => Players[0];
    public long CharacterId => Characters[0].CharacterId;
    public InventoryService Inventory => Harness.World.Inventory!;
    public FakeTransport Transport => Harness.Transport;

    public Npc NpcAt(string markerId) => Harness.World.Entities.OfType<Npc>().Single(n => n.MarkerId == markerId);

    public uint NpcId(string markerId) => NpcAt(markerId).Id;

    public ushort NetId(string itemId)
    {
        Assert.True(Items.TryGetItem(itemId, out var item));
        return (ushort)item.NetId;
    }

    /// <summary>
    /// A class 1 save: asked for in this tick's phase 1 (after the scheduler ran), issued in the next tick, completed at once
    /// by the fake worker and read back in the tick after.
    /// </summary>
    public void Commit() => Harness.Ticks(3);

    /// <summary>The next class 2 batch (every second) and its completion.</summary>
    public void Save()
    {
        Harness.Clock.Advance(1_000);
        Harness.Ticks(2);
    }

    public static ItemStackRow Row(int slot, string item, int count, bool stolen = false) => new(1, slot, item, 0, stolen, count);

    public static WorldMarker Npc(string id, int xCm, int yCm, string npcDefId) =>
        new(id, MarkerKind.Npc, FixVec2.FromCm(xCm, yCm), 0, npcDefId);

    /// <summary>Replaces the rows of table <typeparamref name="T"/>.</summary>
    public static void Replace<T>(ConfigSet set, params T[] rows) where T : class =>
        set.Add(new ConfigTable(typeof(T), 1, rows));

    public static T Single<T>(ConfigSet set) where T : class => set.Single<T>();
}

/// <summary>Records economy events as the bus delivers them (phase 11).</summary>
internal sealed class EventRecorder :
    IEventConsumer<OutpostVisited>, IEventConsumer<ItemSold>, IEventConsumer<GoldChanged>, IEventConsumer<GoldDestroyed>
{
    public List<OutpostVisited> Visits { get; } = [];
    public List<ItemSold> Sales { get; } = [];
    public List<GoldChanged> GoldChanges { get; } = [];
    public List<GoldDestroyed> GoldDestroyed { get; } = [];

    public void Handle(in EventEnvelope envelope, in OutpostVisited payload) => Visits.Add(payload);
    public void Handle(in EventEnvelope envelope, in ItemSold payload) => Sales.Add(payload);
    public void Handle(in EventEnvelope envelope, in GoldChanged payload) => GoldChanges.Add(payload);
    public void Handle(in EventEnvelope envelope, in GoldDestroyed payload) => GoldDestroyed.Add(payload);
}
