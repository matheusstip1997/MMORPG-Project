using GameServer.Economy;
using GameServer.Entities;
using GameServer.Events;
using GameServer.Items;
using GameServer.Items.LootBags;
using GameServer.Net;
using GameServer.Persistence;
using GameServer.Tests.Support;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;
using Shared.Simulation.Items;
using Shared.World;

namespace GameServer.Tests.Items;

/// <summary>
/// The death loot service on the world thread (spec 4.3 R-4.3-03, 4.4 R-4.4-01, M4 decision 8; WP-M4-9), with a real
/// <see cref="InventoryService"/>, a fake world and a fake mail: the death transaction (the Normal bag's stacks leave the
/// character and the bag rows ride the same save; shown only once committed), what drops and what stays, the owner's take with
/// the overflow by mail in mails of at most <c>attachmentsPerMail</c> rows, a party member's collection inside the window, the
/// lock, the stolen mark after the window, two takers in one tick, a faulted taker, the expiry as a counted sink and a restart.
/// </summary>
public sealed class DeathLootServiceTests
{
    private const string Horn = "m1.demonHorn", Ash = "m1.demonAsh";
    private const long Owner = 101, Stranger = 102, Member = 103;

    private sealed class FakeBagWorld : ILootBagWorld
    {
        private uint _nextId = 1_000;
        public Dictionary<uint, Entity> Entities { get; } = new();
        public Dictionary<long, Player> Players { get; } = new();
        public HashSet<(long, long)> Parties { get; } = new();
        public string AreaType { get; set; } = "openWorld";
        public DeathDrop Drops { get; set; } = DeathDrop.On;

        public Entity? FindEntity(uint id) => Entities.TryGetValue(id, out var entity) ? entity : null;
        public Player? PlayerOf(long characterId) => Players.TryGetValue(characterId, out var player) ? player : null;

        public (string RegionId, string AreaType, RegionFlags Flags)? RegionOf(Entity entity) =>
            ("test.field", AreaType, new RegionFlags(PvpMode.ArmedOnly, true, Drops));

        public LootBag AddBag(string bagId, int xCm, int yCm, int heightCm)
        {
            var bag = new LootBag(_nextId++, bagId, new MovementState(FixVec2.FromCm(xCm, yCm), 0), heightCm);
            Entities.Add(bag.Id, bag);
            return bag;
        }

        public void RemoveBag(LootBag bag) => Entities.Remove(bag.Id);
        public bool CanSee(Player observer, Entity target) => true;
        public bool SameParty(long a, long b) => Parties.Contains((a, b)) || Parties.Contains((b, a));

        public Player Add(uint id, long characterId, int xCm = 0, int yCm = 0)
        {
            var player = new Player(id, Player.NoPeer, characterId, new InputQueue(4), new MovementState(FixVec2.FromCm(xCm, yCm), 0), 0,
                characterId);
            Entities.Add(id, player);
            Players.Add(characterId, player);
            return player;
        }
    }

    private sealed class FakeMail(int perMail = 8) : ILootBagMail
    {
        public List<(long Carrier, MailDraft Draft)> Sent { get; } = [];
        public bool Refuse { get; set; }
        public int AttachmentsPerMail => perMail;
        public bool CanSend(long carrier, MailDraft draft) => !Refuse && draft.Items.Count <= perMail;

        public bool Send(long carrier, MailDraft draft)
        {
            Sent.Add((carrier, draft));
            return true;
        }
    }

    private sealed class Fixture : IEventConsumer<LootBagCreated>, IEventConsumer<LootBagPickedUp>, IEventConsumer<LootBagExpired>,
        IEventConsumer<ItemStolen>, IEventConsumer<LootBagNotDropped>
    {
        public Fixture(BagExpiry? expiry = null, int perMail = 8)
        {
            var tables = TestCombat.Tables();
            Items = ItemRules.From(tables)!;
            var config = LootBagConfig.From(tables, Items.Settings)!;
            if (expiry != null)
                config = new LootBagConfig(new Dictionary<string, int> { ["openWorld"] = 60_000 }, expiry, Items.Settings.PickupRangeCm,
                    new Dictionary<string, int> { [Horn] = 10 });
            Config = config;
            Bus = new EventBus(1, Clock, TestPipelineSettings.BusLimits());
            Bus.Subscribe<LootBagCreated>(this);
            Bus.Subscribe<LootBagPickedUp>(this);
            Bus.Subscribe<LootBagExpired>(this);
            Bus.Subscribe<ItemStolen>(this);
            Bus.Subscribe<LootBagNotDropped>(this);
            Ledger.Subscribe(Bus);
            Inventory = new InventoryService(Items, null) { MaxGold = 1_000_000 };
            Mail = new FakeMail(perMail);
            Service = new DeathLootService(Config, Items, Inventory, World, Bus, "run", () => Clock.UtcNowMs)
            {
                RequestSave = (id, reason) => Saves.Add((id, reason)),
                Mail = Mail,
            };
            Service.Subscribe(Bus);
            OwnerPlayer = Join(1, Owner);
            StrangerPlayer = Join(2, Stranger);
            MemberPlayer = Join(3, Member);
        }

        public FakeClock Clock { get; } = new();
        public ItemRules Items { get; }
        public LootBagConfig Config { get; }
        public EventBus Bus { get; }
        public EconomyLedger Ledger { get; } = new();
        public InventoryService Inventory { get; }
        public FakeBagWorld World { get; } = new();
        public FakeMail Mail { get; }
        public DeathLootService Service { get; }
        public List<(long Id, SaveReason Reason)> Saves { get; } = [];
        public List<object> Events { get; } = [];
        public Player OwnerPlayer { get; }
        public Player StrangerPlayer { get; }
        public Player MemberPlayer { get; }

        public void Handle(in EventEnvelope envelope, in LootBagCreated payload) => Events.Add(payload);
        public void Handle(in EventEnvelope envelope, in LootBagPickedUp payload) => Events.Add(payload);
        public void Handle(in EventEnvelope envelope, in LootBagExpired payload) => Events.Add(payload);
        public void Handle(in EventEnvelope envelope, in ItemStolen payload) => Events.Add(payload);
        public void Handle(in EventEnvelope envelope, in LootBagNotDropped payload) => Events.Add(payload);

        private Player Join(uint id, long characterId)
        {
            var player = World.Add(id, characterId);
            Assert.True(Inventory.Attach(characterId, Array.Empty<ItemStackRow>()));
            Inventory.AttachGold(characterId, 0);
            return player;
        }

        public ItemDef Item(string id)
        {
            Assert.True(Items.TryGetItem(id, out var item));
            return item;
        }

        /// <summary>Adds units to a character's bag, committed.</summary>
        public void Give(long characterId, string itemId, int count, string key)
        {
            Assert.True(Inventory.TryAdd(characterId, Item(itemId), 0, count, key, InventoryReason.Admin, "test").Added == count);
            Commit(characterId);
        }

        /// <summary>The character's save: every batch taken, then committed, like the scheduler does.</summary>
        public (InventoryBatch? Inventory, LootBagBatch? Bags) Commit(long characterId)
        {
            var saveId = Guid.NewGuid();
            var inventory = Inventory.TakeBatch(characterId, saveId);
            var bags = Service.TakeBatch(characterId, saveId);
            Inventory.Committed(characterId, saveId);
            Service.Committed(characterId, saveId);
            Bus.DispatchQueued();
            return (inventory, bags);
        }

        public void Die(Player player)
        {
            Bus.Publish(new Died(player.Id, 0), player.CharacterId);
            Bus.DispatchQueued();
        }

        public DeathBag Bag() => Assert.Single(World.Entities.Values.OfType<LootBag>()) is var entity ? Service.Find(entity.Id)! : null!;

        public int Units(long characterId, bool stolen) => Inventory.Current(characterId).CountOfAll(Items, stolen);

        public LootBagResult? LastAnswer(Player player) => player.LootBags.Messages.Count == 0 ? null
            : Read(player.LootBags.Messages[^1]);

        private static LootBagResult Read(byte[] message)
        {
            Assert.True(ReliableMessages.TryRead(message, out LootBagResult result));
            return result;
        }
    }

    [Fact]
    public void A_death_in_a_drop_region_moves_the_normal_bag_into_a_bag_shown_only_once_committed()
    {
        var f = new Fixture();
        f.Give(Owner, Horn, 12, "seed:horn");
        f.Give(Owner, Ash, 5, "seed:ash");
        f.Inventory.TryAddGold(Owner, 50, "seed:gold", InventoryReason.Admin, "test");
        f.Commit(Owner);

        f.Die(f.OwnerPlayer);

        Assert.Empty(f.World.Entities.Values.OfType<LootBag>()); // not before the commit (rule 5)
        Assert.Equal((Owner, SaveReason.Death), Assert.Single(f.Saves));
        Assert.True(f.Inventory.Current(Owner).FirstEmpty() == 0 && f.Inventory.Current(Owner)[0].IsEmpty);
        var (inventory, bags) = f.Commit(Owner);
        // One transaction: the units that left (reason DeathDrop) and the bag rows ride the same save.
        Assert.Contains(inventory!.Ops, o => o.Reason == nameof(InventoryReason.DeathDrop) && o.OpKey.StartsWith("death:run:", StringComparison.Ordinal));
        var created = Assert.Single(bags!.Creates);
        Assert.Equal(new[] { (Horn, 12), (Ash, 5) }, created.Items.Select(i => (i.DefId, i.Count)));
        Assert.Equal(created.CreatedUtcMs + 60_000, created.OwnerWindowUntilUtcMs);
        Assert.Equal(created.CreatedUtcMs + f.Config.Expiry.ExpiryMs, created.ExpiresUtcMs);

        var bag = f.Bag();
        Assert.Equal(BagPhase.Open, bag.Phase);
        var evt = Assert.IsType<LootBagCreated>(Assert.Single(f.Events));
        Assert.Equal((Owner, 17, 120L), (evt.OwnerCharacterId, evt.Units, evt.Value)); // the horns at PriceBase 10, ash without a price
        Assert.Equal(50, f.Inventory.Gold(Owner)); // gold never drops
    }

    [Theory]
    [InlineData(DeathDrop.Off)]
    [InlineData(DeathDrop.Confiscate)]
    public void A_region_without_drops_keeps_the_bag(DeathDrop drops)
    {
        var f = new Fixture();
        f.World.Drops = drops;
        f.Give(Owner, Horn, 3, "seed");
        f.Die(f.OwnerPlayer);

        Assert.Empty(f.Saves);
        Assert.Equal(3, f.Inventory.Current(Owner)[0].Count);
        Assert.Equal(0, f.Service.Count);
    }

    [Fact]
    public void An_empty_normal_bag_drops_nothing_and_a_redelivered_death_drops_once()
    {
        var f = new Fixture();
        f.Die(f.OwnerPlayer);
        Assert.Equal(0, f.Service.Count);

        f.Give(Owner, Horn, 3, "seed");
        var envelope = f.Bus.Publish(new Died(f.OwnerPlayer.Id, 0), Owner);
        f.Bus.DispatchQueued();
        f.Service.Handle(envelope, new Died(f.OwnerPlayer.Id, 0)); // the same event again (rule 3: consumers are idempotent)
        Assert.Equal(1, f.Service.Count);
        Assert.Single(f.Saves);
    }

    [Fact]
    public void Units_a_reservation_holds_stay_with_the_character()
    {
        var f = new Fixture();
        f.Give(Owner, Horn, 12, "seed");
        var at = new InventoryLocation((byte)f.Items.NormalBag.Code, 0);
        Assert.Equal(ReserveResult.Ok, f.Inventory.Reserve(Owner, "trade:1", [new ReserveItem(at, 4)]));

        f.Die(f.OwnerPlayer);
        f.Commit(Owner);

        Assert.Equal(8, f.Bag().Units);
        Assert.Equal(4, f.Inventory.Current(Owner)[0].Count);
    }

    [Fact]
    public void At_the_worlds_cap_a_death_drops_nothing_and_says_so()
    {
        var f = new Fixture(new BagExpiry
        {
            ExpiryMs = 900_000, MaxBagsPerWorld = 1, ExpiryCheckIntervalMs = 1_000, ExpiryRetryMs = 5_000, OwnBagsListedMax = 8,
            MaxQueuedRequests = 4,
        });
        f.Give(Owner, Horn, 3, "seed:a");
        f.Die(f.OwnerPlayer);
        f.Commit(Owner);
        f.Give(Stranger, Horn, 2, "seed:b");

        f.Die(f.StrangerPlayer);

        Assert.Equal(1, f.Service.Count);
        Assert.Equal(1, f.Service.DropsOverCap);
        Assert.Equal(2, f.Inventory.Current(Stranger)[0].Count);
        Assert.Contains(f.Events, e => e is LootBagNotDropped { CharacterId: Stranger, UnitsKept: 2 });
    }

    [Fact]
    public void The_owner_takes_all_back_and_what_does_not_fit_goes_by_mail_in_mails_of_at_most_eight_rows()
    {
        var f = new Fixture();
        f.Give(Owner, Horn, 10 * 99, "seed:horns"); // ten rows
        f.Die(f.OwnerPlayer);
        f.Commit(Owner);
        f.Give(Owner, Ash, 30 * 99, "seed:full"); // the respawned owner's bag is full again
        var bag = f.Bag();
        f.Events.Clear();

        f.Service.Request(f.OwnerPlayer, 1, bag.Entity!.Id, LootBagOp.TakeAll, 0);

        Assert.Null(f.LastAnswer(f.OwnerPlayer)); // answered after the commit
        Assert.Equal(new[] { 8, 2 }, f.Mail.Sent.Select(m => m.Draft.Items.Count));
        Assert.All(f.Mail.Sent, m => Assert.Equal((Owner, Owner, MailKind.LootBagReturn), (m.Carrier, m.Draft.ToCharacterId, m.Draft.Kind)));
        Assert.Equal(2, f.Mail.Sent.Select(m => m.Draft.OpKey).Distinct().Count()); // each mail its own key
        var (_, writes) = f.Commit(Owner);
        var take = Assert.Single(writes!.Takes);
        Assert.Equal(LootBagTakeOutcome.Owner, take.Outcome);
        Assert.Equal(990, take.Rows.Sum(r => r.CountTaken));

        var answer = f.LastAnswer(f.OwnerPlayer)!;
        Assert.Equal((LootBagReason.None, (ushort)0, (ushort)990, false), (answer.Reason, answer.Taken, answer.Mailed, answer.Stolen));
        Assert.Empty(f.World.Entities.Values.OfType<LootBag>()); // emptied: gone
        Assert.Contains(f.Events, e => e is LootBagPickedUp { Outcome: LootBagTakeOutcome.Owner, Mailed: 990 });
    }

    [Fact]
    public void The_owner_takes_back_into_the_normal_bag_with_its_marks_unchanged()
    {
        var f = new Fixture();
        f.Give(Owner, Horn, 7, "seed");
        f.Die(f.OwnerPlayer);
        f.Commit(Owner);

        f.Service.Request(f.OwnerPlayer, 1, f.Bag().Entity!.Id, LootBagOp.TakeAll, 0);
        f.Commit(Owner);

        Assert.Equal(7, f.Inventory.Current(Owner)[0].Count);
        Assert.False(f.Inventory.Current(Owner)[0].Stolen);
        Assert.Empty(f.Mail.Sent);
    }

    [Fact]
    public void A_party_member_inside_the_window_collects_for_the_owner_by_mail_with_no_mark()
    {
        var f = new Fixture();
        f.World.Parties.Add((Owner, Member));
        f.Give(Owner, Horn, 5, "seed");
        f.Die(f.OwnerPlayer);
        f.Commit(Owner);

        f.Service.Request(f.MemberPlayer, 1, f.Bag().Entity!.Id, LootBagOp.TakeAll, 0);
        f.Commit(Member);

        var (carrier, draft) = Assert.Single(f.Mail.Sent);
        Assert.Equal((Member, Owner), (carrier, draft.ToCharacterId));
        Assert.False(Assert.Single(draft.Items).Stolen);
        Assert.True(f.Inventory.Current(Member)[0].IsEmpty); // nothing went to the member
        Assert.Contains(f.Events, e => e is LootBagPickedUp { Outcome: LootBagTakeOutcome.ForOwner, CharacterId: Member });
        Assert.DoesNotContain(f.Events, e => e is ItemStolen);
    }

    [Fact]
    public void A_stranger_inside_the_window_is_locked_out_and_nothing_moves()
    {
        var f = new Fixture();
        f.Give(Owner, Horn, 5, "seed");
        f.Die(f.OwnerPlayer);
        f.Commit(Owner);

        f.Service.Request(f.StrangerPlayer, 1, f.Bag().Entity!.Id, LootBagOp.TakeAll, 0);

        Assert.Equal(LootBagReason.BagLocked, f.LastAnswer(f.StrangerPlayer)!.Reason);
        Assert.Equal(5, f.Bag().Units);
        Assert.Null(f.Service.TakeBatch(Stranger, Guid.NewGuid()));
    }

    [Theory]
    [InlineData(Stranger)]
    [InlineData(Member)] // a party member after the window takes with the mark too (M4 decision 8)
    public void After_the_window_a_non_owner_takes_with_the_stolen_mark_and_the_stacks_never_merge(long taker)
    {
        var f = new Fixture();
        f.World.Parties.Add((Owner, Member));
        f.Give(Owner, Horn, 5, "seed:owner");
        f.Die(f.OwnerPlayer);
        f.Commit(Owner);
        f.Give(taker, Horn, 3, "seed:taker"); // a clean stack of the same item
        f.Clock.Advance(60_000);
        var player = f.World.PlayerOf(taker)!;
        long at = f.Clock.UtcNowMs;

        f.Service.Request(player, 1, f.Bag().Entity!.Id, LootBagOp.TakeAll, 0);
        Assert.DoesNotContain(f.Events, e => e is ItemStolen); // not before the commit
        f.Commit(taker);

        var bag = f.Inventory.Current(taker);
        Assert.Equal((3, false), (bag[0].Count, bag[0].Stolen));
        Assert.Equal((5, true, Owner, at), (bag[1].Count, bag[1].Stolen, bag[1].StolenFrom, bag[1].StolenAtMs));
        var answer = f.LastAnswer(player)!;
        Assert.Equal((LootBagReason.None, (ushort)5, true), (answer.Reason, answer.Taken, answer.Stolen));
        var stolen = Assert.IsType<ItemStolen>(Assert.Single(f.Events, e => e is ItemStolen));
        Assert.Equal((taker, Owner, 5, at), (stolen.CharacterId, stolen.OriginalOwnerId, stolen.Count, stolen.AtUtcMs));
        Assert.Empty(f.Mail.Sent); // a stranger's overflow stays in the bag; nothing is mailed
    }

    [Fact]
    public void A_stranger_takes_what_fits_and_the_rest_stays_in_the_bag()
    {
        var f = new Fixture();
        f.Give(Owner, Horn, 2 * 99, "seed:owner");
        f.Die(f.OwnerPlayer);
        f.Commit(Owner);
        f.Give(Stranger, Ash, 29 * 99, "seed:full"); // one slot left
        f.Clock.Advance(60_000);

        f.Service.Request(f.StrangerPlayer, 1, f.Bag().Entity!.Id, LootBagOp.TakeAll, 0);
        f.Commit(Stranger);

        Assert.Equal(99, f.Bag().Units);
        Assert.Equal((ushort)99, f.LastAnswer(f.StrangerPlayer)!.Taken);
    }

    [Fact]
    public void Two_takers_in_one_tick_never_take_one_row_twice()
    {
        var f = new Fixture();
        f.Give(Owner, Horn, 5, "seed");
        f.Die(f.OwnerPlayer);
        f.Commit(Owner);
        f.Clock.Advance(60_000);
        uint bag = f.Bag().Entity!.Id;

        // Phase 1 in entity id order: the owner (1) first, then the stranger (2) finds the row in flight.
        f.Service.Request(f.OwnerPlayer, 1, bag, LootBagOp.TakeAll, 0);
        f.Service.Request(f.StrangerPlayer, 1, bag, LootBagOp.TakeAll, 0);

        Assert.Equal(LootBagReason.BagEmpty, f.LastAnswer(f.StrangerPlayer)!.Reason);
        f.Commit(Owner);
        Assert.Equal(5, f.Inventory.Current(Owner)[0].Count);
        Assert.True(f.Inventory.Current(Stranger)[0].IsEmpty);
    }

    [Fact]
    public void A_taker_whose_save_never_ran_leaves_the_rows_in_the_bag()
    {
        var f = new Fixture();
        f.Give(Owner, Horn, 5, "seed");
        f.Die(f.OwnerPlayer);
        f.Commit(Owner);
        f.Clock.Advance(60_000);
        uint bag = f.Bag().Entity!.Id;

        f.Service.Request(f.StrangerPlayer, 1, bag, LootBagOp.TakeAll, 0);
        Assert.Equal(0, f.Bag().Units);
        f.Service.Forget(Stranger); // the session faulted before its save: the database never saw the take

        Assert.Equal(5, f.Bag().Units);
        f.Service.Request(f.OwnerPlayer, 2, bag, LootBagOp.TakeAll, 0);
        f.Commit(Owner);
        Assert.Equal(5, f.Inventory.Current(Owner)[0].Count);
    }

    [Fact]
    public void An_expired_bag_leaves_the_world_and_its_units_count_as_destroyed()
    {
        var f = new Fixture();
        f.Give(Owner, Horn, 6, "seed");
        f.Die(f.OwnerPlayer);
        f.Commit(Owner);
        f.Events.Clear();

        f.Clock.Advance(f.Config.Expiry.ExpiryMs - 1);
        f.Service.ExpireDue();
        Assert.Single(f.World.Entities.Values.OfType<LootBag>());

        f.Clock.Advance(f.Config.Expiry.ExpiryCheckIntervalMs);
        f.Service.ExpireDue();
        Assert.Empty(f.World.Entities.Values.OfType<LootBag>());
        f.Service.Tick();
        f.Bus.DispatchQueued();

        var expired = Assert.IsType<LootBagExpired>(Assert.Single(f.Events));
        Assert.Equal((6, 60L), (expired.Units, expired.Value));
        Assert.Equal(6, f.Ledger.ItemsDestroyedBy(InventoryReason.BagExpired));
        Assert.Equal(0, f.Service.Count);
    }

    [Fact]
    public void A_bag_with_a_take_in_flight_waits_for_the_commit_before_it_expires()
    {
        var f = new Fixture();
        f.Give(Owner, Horn, 2 * 99, "seed");
        f.Die(f.OwnerPlayer);
        f.Commit(Owner);
        f.Service.Request(f.OwnerPlayer, 1, f.Bag().Entity!.Id, LootBagOp.TakeOne, 0);
        f.Clock.Advance(f.Config.Expiry.ExpiryMs + 1);

        f.Service.ExpireDue();
        Assert.Single(f.World.Entities.Values.OfType<LootBag>()); // its take holds it

        f.Commit(Owner);
        f.Clock.Advance(f.Config.Expiry.ExpiryCheckIntervalMs);
        f.Service.ExpireDue();
        f.Service.Tick();
        f.Bus.DispatchQueued();
        Assert.Equal(99, f.Ledger.ItemsDestroyedBy(InventoryReason.BagExpired));
    }

    [Fact]
    public void A_restart_spawns_the_open_bags_again_and_they_can_be_taken()
    {
        var f = new Fixture();
        long now = f.Clock.UtcNowMs;
        f.Service.Load([new LootBagRecord("death:old:7", Owner, "test.field", 300, 400, 0, now - 10_000, now + 50_000, now + 800_000,
            [new LootBagItemRow(0, Horn, 0, 4, false), new LootBagItemRow(1, Ash, 0, 2, true, Stranger, now - 20_000)])]);

        var bag = f.Bag();
        Assert.Equal((300, 400, 6), (bag.Entity!.XCm, bag.Entity.YCm, bag.Units));
        f.Service.Request(f.OwnerPlayer, 1, bag.Entity.Id, LootBagOp.TakeAll, 0);
        f.Commit(Owner);

        var stacks = f.Inventory.Current(Owner);
        Assert.Equal((4, false), (stacks[0].Count, stacks[0].Stolen));
        Assert.Equal((2, true, Stranger), (stacks[1].Count, stacks[1].Stolen, stacks[1].StolenFrom)); // its first mark kept
    }

    [Fact]
    public void A_take_far_from_the_bag_or_while_dead_is_refused()
    {
        var f = new Fixture();
        f.Give(Owner, Horn, 2, "seed");
        f.Die(f.OwnerPlayer);
        f.Commit(Owner);
        uint bag = f.Bag().Entity!.Id;
        f.OwnerPlayer.Movement = new MovementState(FixVec2.FromCm(f.Items.Settings.PickupRangeCm + 1, 0), 0);

        f.Service.Request(f.OwnerPlayer, 1, bag, LootBagOp.TakeAll, 0);
        Assert.Equal(LootBagReason.OutOfRange, f.LastAnswer(f.OwnerPlayer)!.Reason);
        f.Service.Request(f.OwnerPlayer, 2, 999_999, LootBagOp.TakeAll, 0);
        Assert.Equal(LootBagReason.BagNotFound, f.LastAnswer(f.OwnerPlayer)!.Reason);
    }
}

/// <summary>Counting helpers for the bag tests.</summary>
internal static class BagCounts
{
    public static int CountOfAll(this Bag bag, ItemRules items, bool stolen)
    {
        int total = 0;
        for (int slot = 0; slot < bag.SlotCount; slot++)
        {
            if (!bag[slot].IsEmpty && bag[slot].Stolen == stolen)
                total += bag[slot].Count;
        }
        return total;
    }
}
