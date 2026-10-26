using GameServer.Entities;
using GameServer.Events;
using GameServer.Items;
using GameServer.Persistence;
using GameServer.Tests.Support;
using GameServer.World;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;
using Shared.Simulation.Items;
using Shared.Util;

namespace GameServer.Tests.Items;

/// <summary>
/// Loot and pickup in the world tick by tick (backlog G, R-4.3-01, R-4.3-02 M1): a kill drops the rolled loot where the
/// monster died, locked to the killer; pickups are checked (alive, range, lock, space), take what fits, and reach the
/// client only after the class 2 save commits (rule 5); a re-dispatched kill drops nothing twice; of two players asking
/// for one drop in the same tick exactly one gets it.
/// </summary>
public class LootWorldTests
{
    private const ulong Seed = 42;

    private sealed class Fixture
    {
        public Fixture(Action<ConfigSet>? edit = null, int lingerMs = 0)
        {
            var tables = TestCombat.Tables();
            edit?.Invoke(tables);
            var combat = TestCombat.Rules(tables, TestCombat.Point());
            Items = ItemRules.From(tables)!;
            Bus = new EventBus(1, new FakeClock(), TestPipelineSettings.BusLimits());
            Harness = new WorldHarness(map: TestCombat.OpenMap(TestCombat.MonsterMarker(2_000, 0)),
                events: new EventDispatchHook(Bus), bus: Bus, combat: combat, items: Items, rngSeed: Seed,
                values: TestSessions.Values(lingerOutOfCombatMs: lingerMs));
        }

        public WorldHarness Harness { get; }
        public EventBus Bus { get; }
        public ItemRules Items { get; }
        public EventLog Log { get; } = new();
        public GameWorld World => Harness.World;
        public InventoryService Inventory => World.Inventory!;

        public Monster Monster() => Assert.Single(World.Entities.OfType<Monster>());

        public List<LootDrop> Drops() => World.Entities.OfType<LootDrop>().ToList();

        /// <summary>A kill as HitResolver publishes it, delivered in this tick's phase 11.</summary>
        public void Kill(Monster monster, long killerCharacterId, uint killerId = 1, int xCm = 0, int yCm = 0)
        {
            Bus.Publish(new Killed(killerId, monster.Id, 0, monster.DefId, xCm, yCm), killerCharacterId);
            Harness.Ticks(1);
        }

        /// <summary>The next class 2 batch (every second) and its completion.</summary>
        public void Save()
        {
            Harness.Clock.Advance(1_000);
            Harness.Ticks(2);
        }
    }

    private sealed class EventLog : IEventConsumer<LootDropped>, IEventConsumer<LootPickedUp>, IEventConsumer<ItemCreated>,
        IEventConsumer<LootDropExpired>
    {
        public List<object> Events { get; } = [];
        public void Handle(in EventEnvelope envelope, in LootDropped payload) => Events.Add(payload);
        public void Handle(in EventEnvelope envelope, in LootPickedUp payload) => Events.Add(payload);
        public void Handle(in EventEnvelope envelope, in ItemCreated payload) => Events.Add(payload);
        public void Handle(in EventEnvelope envelope, in LootDropExpired payload) => Events.Add(payload);
    }

    private static void Subscribe(EventBus bus, EventLog log)
    {
        bus.Subscribe<LootDropped>(log);
        bus.Subscribe<LootPickedUp>(log);
        bus.Subscribe<ItemCreated>(log);
        bus.Subscribe<LootDropExpired>(log);
    }

    private static List<ItemStack> Expected(ItemRules items, Monster monster, ulong seed)
    {
        Assert.True(items.TryGetLoot(monster.DefId, out var loot));
        var drops = new List<ItemStack>();
        LootRoll.Roll(loot.Choices, loot.Rolls, loot.NothingWeight, new Pcg32Rng(seed, GameWorld.LootStreamOffset), drops);
        return drops;
    }

    private static int Units(InventoryService inventory, Player player) =>
        Enumerable.Range(0, inventory.Rules.NormalBag.Slots).Sum(i => inventory.Current(player.CharacterId)[i].Count);

    [Fact]
    public void A_kill_drops_the_seeded_roll_where_the_monster_died_locked_to_the_killer()
    {
        var f = new Fixture();
        Subscribe(f.Bus, f.Log);
        var player = f.Harness.Join(1);
        var monster = f.Monster();
        long now = f.World.NowMs;

        f.Kill(monster, player.CharacterId, player.Id, xCm: 100, yCm: 0);

        var expected = Expected(f.Items, monster, Seed);
        var drops = f.Drops();
        Assert.Equal(expected.Count, drops.Count);
        Assert.Equal(expected, drops.Select(d => new ItemStack((ushort)d.Item.NetId, d.Rarity, false, d.Count)));
        Assert.All(drops, d =>
        {
            Assert.Equal(EntityKind.LootDrop, d.Kind);
            Assert.Equal((ushort)d.Item.NetId, d.DefId);
            Assert.Equal(player.CharacterId, d.OwnerCharacterId);
            Assert.InRange(d.ExclusiveUntilMs - now, f.Items.LootLockMs, f.Items.LootLockMs + 100);
            Assert.InRange(d.ExpiresAtMs - now, f.Items.DropExpiryMs, f.Items.DropExpiryMs + 100);
            long dx = d.XCm - 100, dy = d.YCm;
            Assert.True(dx * dx + dy * dy <= (long)(f.Items.Settings.DropScatterCm + 1) * (f.Items.Settings.DropScatterCm + 1));
        });
        Assert.Equal(expected.Count, f.Log.Events.OfType<LootDropped>().Count());
    }

    [Fact]
    public void A_drop_reaches_nearby_clients_as_a_LootDrop_spawn_with_its_item()
    {
        var f = new Fixture();
        var player = f.Harness.Join(1);
        f.Kill(f.Monster(), player.CharacterId, xCm: 100);
        f.Harness.Ticks(1);

        var drop = f.Drops()[0];
        var spawn = f.Harness.Transport.SnapshotsFor(1).SelectMany(s => s.Spawns).Single(s => s.EntityId == drop.Id);
        Assert.Equal(EntityKind.LootDrop, spawn.Kind);
        Assert.Equal(drop.DefId, spawn.DefId);
    }

    [Fact]
    public void A_redispatched_kill_drops_nothing_twice()
    {
        var f = new Fixture();
        var player = f.Harness.Join(1);
        var monster = f.Monster();
        var envelope = f.Bus.Publish(new Killed(player.Id, monster.Id, 0, monster.DefId, 100, 0), player.CharacterId);
        f.Harness.Ticks(1);
        int drops = f.Drops().Count;
        Assert.True(drops > 0);

        // The same event delivered again (a replay after a consumer fault, or a bug): ignored by event id.
        f.World.Loot!.Handle(envelope, new Killed(player.Id, monster.Id, 0, monster.DefId, 100, 0));

        Assert.Equal(drops, f.Drops().Count);
    }

    [Fact]
    public void The_killer_picks_up_and_the_client_hears_of_it_only_after_the_save_commits()
    {
        var f = new Fixture();
        Subscribe(f.Bus, f.Log);
        var player = f.Harness.Join(1);
        f.Kill(f.Monster(), player.CharacterId, xCm: 100);
        var drop = f.Drops()[0];
        int count = drop.Count;
        var before = f.Harness.Transport.InventoryFor(1);
        Assert.Single(before); // the bag of the new connection (empty)
        Assert.Empty(before[0].Slots);

        f.Harness.SendPickup(1, drop.Id);
        f.Harness.Ticks(1);

        var result = Assert.Single(f.Harness.Transport.PickupResultsFor(1));
        Assert.Equal(PickupReason.None, result.Reason);
        Assert.Equal(count, result.Taken);
        Assert.DoesNotContain(drop, f.World.Entities); // gone from the ground at once: nobody else can take it
        Assert.Equal(count, Units(f.Inventory, player));
        Assert.True(f.Inventory.Committed(player.CharacterId).All(s => s.IsEmpty)); // not in the database yet
        Assert.Single(f.Harness.Transport.InventoryFor(1)); // so the client was not told
        Assert.Empty(f.Log.Events.OfType<ItemCreated>());

        f.Save();

        var job = Assert.Single(f.Harness.Saves.Jobs, j => j.Inventory != null);
        var op = Assert.Single(job.Inventory!.Ops);
        Assert.StartsWith("test-run:", op.OpKey);
        Assert.Equal(nameof(InventoryReason.LootPickup), op.Reason);
        var contents = f.Harness.Transport.InventoryFor(1)[^1];
        var slot = Assert.Single(contents.Slots);
        Assert.Equal((drop.DefId, (ushort)count), (slot.ItemNetId, slot.Count));
        var created = Assert.Single(f.Log.Events.OfType<ItemCreated>());
        Assert.Equal((player.CharacterId, count, InventoryReason.LootPickup), (created.CharacterId, created.Count, created.Reason));
    }

    [Fact]
    public void Others_cannot_take_a_locked_drop_until_the_lock_ends()
    {
        var f = new Fixture();
        var killer = f.Harness.Join(1);
        var other = f.Harness.Join(2);
        f.Kill(f.Monster(), killer.CharacterId, xCm: 100);
        var drop = f.Drops()[0];

        f.Harness.SendPickup(2, drop.Id);
        f.Harness.Ticks(1);
        Assert.Equal(PickupReason.LootLocked, Assert.Single(f.Harness.Transport.PickupResultsFor(2)).Reason);
        Assert.Contains(drop, f.World.Entities);

        int lockTicks = f.Items.LootLockMs / (1000 / f.World.Settings.TickRateHz);
        f.Harness.Ticks(lockTicks);
        f.Harness.SendPickup(2, drop.Id);
        f.Harness.Ticks(1);

        Assert.Equal(PickupReason.None, f.Harness.Transport.PickupResultsFor(2)[^1].Reason);
        Assert.True(Units(f.Inventory, other) > 0);
        Assert.Equal(0, Units(f.Inventory, killer));
    }

    [Fact]
    public void Two_players_asking_for_one_drop_in_the_same_tick_get_it_exactly_once()
    {
        var f = new Fixture();
        var first = f.Harness.Join(1);
        var second = f.Harness.Join(2);
        f.Kill(f.Monster(), killerCharacterId: 0, xCm: 100); // a monster's kill: no owner, nobody is locked out
        var drop = f.Drops()[0];
        int count = drop.Count;

        f.Harness.SendPickup(2, drop.Id); // arrival order does not matter: players go in entity id order
        f.Harness.SendPickup(1, drop.Id);
        f.Harness.Ticks(1);

        Assert.Equal(PickupReason.None, Assert.Single(f.Harness.Transport.PickupResultsFor(1)).Reason);
        Assert.Equal(PickupReason.LootNotFound, Assert.Single(f.Harness.Transport.PickupResultsFor(2)).Reason);
        Assert.Equal(count, Units(f.Inventory, first) + Units(f.Inventory, second));
        Assert.Equal(count, Units(f.Inventory, first));
    }

    [Fact]
    public void A_drop_out_of_range_or_unknown_or_while_dead_is_refused()
    {
        var f = new Fixture();
        var player = f.Harness.Join(1); // at the origin
        f.Kill(f.Monster(), player.CharacterId, xCm: 1_000); // 10 m away, the range is 3 m
        var drop = f.Drops()[0];

        f.Harness.SendPickup(1, drop.Id);
        f.Harness.SendPickup(1, 999_999);
        f.Harness.Ticks(1);
        player.Combatant!.Meters.Drain(Shared.Simulation.Stats.ResourceKind.Hp, 1_000_000, f.World.NowMs);
        player.Combatant.Die(f.World.ServerTick);
        f.Harness.SendPickup(1, drop.Id);
        f.Harness.Ticks(1);

        Assert.Equal(new[] { PickupReason.OutOfRange, PickupReason.LootNotFound, PickupReason.PickupWhileDead },
            f.Harness.Transport.PickupResultsFor(1).Select(r => r.Reason));
        Assert.Contains(drop, f.World.Entities);
    }

    [Fact]
    public void A_partial_pickup_leaves_the_rest_of_the_same_drop_for_a_later_pickup()
    {
        var f = new Fixture(tables => SetDemonLoot(tables, countMin: 3, countMax: 3));
        var player = f.Harness.Join(1);
        Assert.True(f.Items.TryGetItem("m1.demonHorn", out var horn));
        Assert.True(f.Items.TryGetItem("m1.demonAsh", out var ash));
        for (int i = 0; i < f.Items.NormalBag.Slots - 1; i++)
            f.Inventory.TryAdd(player.CharacterId, ash, 0, ash.MaxStack, $"fill:{i}", InventoryReason.Admin, "test");
        f.Inventory.TryAdd(player.CharacterId, horn, 0, horn.MaxStack - 1, "fill:last", InventoryReason.Admin, "test");
        f.Kill(f.Monster(), player.CharacterId, xCm: 100);
        var drop = Assert.Single(f.Drops());

        f.Harness.SendPickup(1, drop.Id);
        f.Harness.Ticks(1);
        Assert.Equal((PickupReason.None, (ushort)1), (f.Harness.Transport.PickupResultsFor(1)[^1].Reason, f.Harness.Transport.PickupResultsFor(1)[^1].Taken));
        Assert.Equal(2, drop.Count);
        Assert.Contains(drop, f.World.Entities);

        f.Harness.SendPickup(1, drop.Id);
        f.Harness.Ticks(1);
        Assert.Equal(PickupReason.BagFull, f.Harness.Transport.PickupResultsFor(1)[^1].Reason);

        Assert.True(f.Inventory.TryRemove(player.CharacterId, f.Items.NormalBag.Slots - 1, horn.MaxStack, "free", InventoryReason.Admin, "test"));
        f.Harness.SendPickup(1, drop.Id);
        f.Harness.Ticks(1);
        Assert.Equal((PickupReason.None, (ushort)2), (f.Harness.Transport.PickupResultsFor(1)[^1].Reason, f.Harness.Transport.PickupResultsFor(1)[^1].Taken));
        Assert.DoesNotContain(drop, f.World.Entities);
    }

    [Fact]
    public void A_drop_nobody_takes_expires()
    {
        var f = new Fixture(tables => ((StartingValuesM1)Table(tables, typeof(StartingValuesM1)).Rows[0]).BagExpiryMs = 1_000);
        Subscribe(f.Bus, f.Log);
        var player = f.Harness.Join(1);
        f.Kill(f.Monster(), player.CharacterId, xCm: 100);
        int drops = f.Drops().Count;

        f.Harness.Ticks(f.World.Settings.TickRateHz + 1);

        Assert.Empty(f.Drops());
        Assert.Equal(drops, f.Log.Events.OfType<LootDropExpired>().Count());
    }

    [Fact]
    public void Picked_up_items_are_saved_and_come_back_after_a_reconnect()
    {
        var f = new Fixture();
        var player = f.Harness.Join(1);
        long characterId = player.CharacterId;
        f.Kill(f.Monster(), characterId, xCm: 100);
        var drop = f.Drops()[0];
        int count = drop.Count;
        f.Harness.SendPickup(1, drop.Id);
        f.Harness.Ticks(1);

        f.Harness.Leave(1); // no linger: the logout save carries the batch, then the character leaves
        f.Harness.Ticks(3);
        Assert.False(f.Inventory.IsAttached(characterId));
        var stored = Assert.Single(f.Harness.Characters.Items[characterId]);
        Assert.Equal((count, f.Items.NormalBag.Code), (stored.Count, stored.Container));

        f.Harness.Join(1);
        f.Harness.Ticks(1);
        var contents = f.Harness.Transport.InventoryFor(1)[^1];
        Assert.Equal((ushort)count, Assert.Single(contents.Slots).Count);
    }

    [Fact]
    public void A_login_with_an_item_the_tables_do_not_know_is_refused()
    {
        var f = new Fixture();
        var character = f.Harness.Characters.Add(1, 0, new CharacterState(1, 0, "", 0, 0, 0, 0, 100, 50, 100));
        f.Harness.Characters.Items[character.CharacterId] = [new ItemStackRow(1, 0, "m1.removedItem", 0, false, 1)];

        f.Harness.Connect(1);
        f.Harness.Ticks(2);

        Assert.Contains((1, ConnectRejectReason.CharacterUnavailable), f.Harness.Transport.Kicks);
        Assert.False(f.Inventory.IsAttached(character.CharacterId));
    }

    private static ConfigTable Table(ConfigSet tables, Type type)
    {
        Assert.True(tables.TryGet(type, out var table));
        return table;
    }

    private static void SetDemonLoot(ConfigSet tables, int countMin, int countMax)
    {
        var entries = Table(tables, typeof(LootTableEntry)).Rows.Cast<LootTableEntry>().ToList();
        foreach (var entry in entries.Where(e => e.TableId == "m1.demon"))
        {
            entry.Weight = entry.ItemId == "m1.demonHorn" ? 1 : entry.Weight;
            entry.CountMin = countMin;
            entry.CountMax = countMax;
        }
        entries.RemoveAll(e => e.TableId == "m1.demon" && e.ItemId != "m1.demonHorn");
        tables.Add(new ConfigTable(typeof(LootTableEntry), 1, entries.Cast<object>().ToList()));
    }
}
