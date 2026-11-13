using GameServer.Economy;
using GameServer.Entities;
using GameServer.Events;
using GameServer.Items;
using GameServer.Items.Mail;
using GameServer.Persistence;
using GameServer.Tests.Economy;
using GameServer.Tests.Support;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;


namespace GameServer.Tests.Items;

/// <summary>
/// Mail in the world (spec 4.5 M4 mail, 9.3; WP-M4-3) with the repository tables: the count of the M badge at enter world, the
/// list, a claim answered and published (<c>MailClaimed</c>) only after its save committed, the rate limit of spec 7.5, the
/// mail worker's reads and the expiry job's mails published as a sink counted by <see cref="EconomyLedger"/>. WP-M4-14
/// (R-4.5-03, M4 decision 6a): claims are taken at a mailbox in reach, the list and a mail are read anywhere.
/// </summary>
public class MailWorldTests
{
    private const long MailId = 40;
    private const string Mailbox = "test.npc.mailbox";
    private const string FarMailbox = "test.npc.mailbox.far";
    private const string Outpost = "test.npc.outpost";

    private sealed class FakeMailJobs : IMailJobs
    {
        public Queue<MailReloaded> Reloaded { get; } = new();
        public Queue<MailExpiredRow> Expired { get; } = new();
        public List<(long CharacterId, int Version)> Requests { get; } = [];

        public void RequestReload(long characterId, int version) => Requests.Add((characterId, version));
        public bool TryTakeReloaded(out MailReloaded reloaded) => Reloaded.TryDequeue(out reloaded);
        public bool TryTakeExpired(out MailExpiredRow expired) => Expired.TryDequeue(out expired!);
    }

    private sealed class Fixture : IEventConsumer<MailClaimed>, IEventConsumer<MailExpired>
    {
        public Fixture(int open = 1)
        {
            var tables = TestCombat.Tables();
            Bus = new EventBus(1, new FakeClock(), TestPipelineSettings.BusLimits());
            Bus.Subscribe<MailClaimed>(this);
            Bus.Subscribe<MailExpired>(this);
            Ledger.Subscribe(Bus);
            var items = ItemRules.From(tables)!;
            Rules = MailRules.From(tables, items)!;
            // WP-M4-14: a mailbox in reach of the spawn (the existing claims run there), a far one and an outpost next to it.
            var map = TestMaps.Map(TestMaps.Open().Grid,
            [
                TestMaps.SpawnMarker(0, 0), EconomyFixture.Npc(Mailbox, 0, 200, "m1.mailbox"),
                EconomyFixture.Npc(Outpost, 200, 0, "m1.outpost"), EconomyFixture.Npc(FarMailbox, 5_000, 5_000, "m1.mailbox"),
            ]);
            Harness = new WorldHarness(map: map, events: new EventDispatchHook(Bus), bus: Bus, items: items,
                economy: EconomyRules.From(tables)!, mail: Rules, mailJobs: Jobs);
            var character = Harness.Characters.Add(1, 0, new CharacterState(5, 0, "", 0, 0, 0, 0, 100, 50, 100));
            long now = Harness.Clock.UtcNowMs;
            var mails = Enumerable.Range(0, open).Select(i => new MailRow(MailId + i, character.CharacterId, MailKind.ListingProceeds,
                MailSubjects.ListingProceeds, [], 25, now - 1_000 + i, now + Rules.Settings.ExpiryDurationMs(),
                [new MailItemRow(0, "m1.demonAsh", 0, 4, 0, false, false, null)])).ToList();
            Harness.Characters.MailRecords[character.CharacterId] = new MailRecord(mails, open);
            Player = Harness.Join(1);
            Harness.Ticks(1);
        }

        public WorldHarness Harness { get; }
        public EventBus Bus { get; }
        public EconomyLedger Ledger { get; } = new();
        public MailRules Rules { get; }
        public FakeMailJobs Jobs { get; } = new();
        public Player Player { get; }
        public FakeTransport Transport => Harness.Transport;
        public List<object> Events { get; } = [];

        public uint NpcId(string markerId) => Harness.World.Entities.OfType<Npc>().Single(n => n.MarkerId == markerId).Id;

        public uint MailboxId => NpcId(Mailbox);

        public void MoveTo(int xCm, int yCm) => Player.Movement = new MovementState(FixVec2.FromCm(xCm, yCm), Player.Movement.Facing);

        public void Handle(in EventEnvelope envelope, in MailClaimed payload) => Events.Add(payload);
        public void Handle(in EventEnvelope envelope, in MailExpired payload) => Events.Add(payload);
    }

    [Fact]
    public void Enter_world_sends_the_count_and_a_list_answers_from_the_loaded_mails()
    {
        var f = new Fixture(open: 2);

        var count = Assert.Single(f.Transport.MailCountsFor(1));
        Assert.Equal(((ushort)2, (ushort)f.Rules.Settings.MailboxCap), (count.Open, count.Cap));
        f.Harness.SendMailOpen(1, 7);
        f.Harness.Ticks(1);

        var list = Assert.Single(f.Transport.MailListsFor(1));
        Assert.Equal((7u, MailReason.None, (ushort)2), (list.RequestId, list.Reason, list.Total));
        Assert.Equal(new[] { MailId, MailId + 1 }, list.Mails.Select(m => m.MailId));
        Assert.Equal(4, list.Mails[0].Items.Single().Count);
        Assert.Empty(f.Jobs.Requests); // every open mail is loaded: no database read

        f.Harness.SendMailOpen(1, 8, MailId);
        f.Harness.Ticks(1);
        var content = Assert.Single(f.Transport.MailContentsFor(1));
        Assert.Equal((MailReason.None, (byte)0, (ushort)4), (content.Reason, content.Items.Single().Slot, content.Items.Single().Count));
    }

    [Fact]
    public void A_claim_is_answered_and_published_only_after_its_save_commits()
    {
        var f = new Fixture();
        f.Harness.Saves.AutoComplete = false;
        f.Harness.SendMailClaim(1, 3, MailId, f.MailboxId);
        f.Harness.Ticks(2); // the save is issued, not completed

        Assert.Empty(f.Transport.MailResultsFor(1));
        Assert.Empty(f.Events);
        var save = f.Harness.Saves.Jobs.Single(j => j.Mail is { Claims.Count: > 0 });
        Assert.Equal(SaveReason.MailClaim, save.Reason);
        var claim = Assert.Single(save.Mail!.Claims);
        Assert.Contains(save.Inventory!.Ops, o => o.OpKey == claim.OpKey + ":g"); // one transaction (rule 5)

        f.Harness.Saves.CompleteAll();
        f.Harness.Ticks(2);

        var answer = Assert.Single(f.Transport.MailResultsFor(1));
        Assert.Equal((3u, MailReason.None, 25L, (ushort)1), (answer.RequestId, answer.Reason, answer.GoldTaken, answer.MailsEmptied));
        var claimed = Assert.IsType<MailClaimed>(Assert.Single(f.Events));
        Assert.Equal((MailId, 25L, true), (claimed.MailId, claimed.Gold, claimed.Complete));
        Assert.Equal((ushort)0, f.Transport.MailCountsFor(1)[^1].Open); // the badge follows
        Assert.Equal(0, f.Ledger.GoldCreated); // a claim moves gold: no faucet
    }

    [Fact]
    public void Mail_requests_are_rate_limited()
    {
        var f = new Fixture();
        int burst = f.Rules.Settings.Burst;
        for (uint i = 1; i <= burst + 1; i++)
            f.Harness.SendMailOpen(1, i);
        f.Harness.Ticks(1);

        var lists = f.Transport.MailListsFor(1);
        Assert.Equal(burst + 1, lists.Count);
        Assert.Equal(MailReason.MailRateLimited, lists[^1].Reason);
        Assert.All(lists.Take(burst), l => Assert.Equal(MailReason.None, l.Reason));
    }

    [Fact]
    public void A_list_past_the_loaded_mails_waits_for_the_workers_read()
    {
        var f = new Fixture();
        // One mail loaded but three open: the read past the limit comes from the mail worker.
        f.Harness.Characters.MailRecords.Clear();
        var world = f.Harness.World;
        long id = f.Player.CharacterId;
        int version = world.Mail!.Version(id);
        world.Mail.ApplyReload(id, new MailRecord(world.Mail.Mailbox(id), 3), version);
        f.Harness.SendMailOpen(1, 9);
        f.Harness.Ticks(1);
        Assert.Empty(f.Transport.MailListsFor(1));
        var request = Assert.Single(f.Jobs.Requests);

        f.Jobs.Reloaded.Enqueue(new MailReloaded(id, request.Version, new MailRecord(world.Mail.Mailbox(id), 1)));
        f.Harness.Ticks(1);

        var list = Assert.Single(f.Transport.MailListsFor(1));
        Assert.Equal((9u, (ushort)1), (list.RequestId, list.Total));
    }

    [Fact]
    public void An_expired_mail_is_published_once_and_counted_as_a_sink()
    {
        var f = new Fixture();
        f.Jobs.Expired.Enqueue(new MailExpiredRow(MailId, f.Player.CharacterId, MailKind.ListingProceeds, 25,
            [new MailItemRow(0, "m1.demonAsh", 0, 4, 0, false, false, null)]));
        f.Harness.Ticks(2);

        var expired = Assert.IsType<MailExpired>(Assert.Single(f.Events));
        Assert.Equal((MailId, 25L), (expired.MailId, expired.Gold));
        Assert.Equal(25, f.Ledger.GoldDestroyedBy(InventoryReason.MailExpired));
        Assert.Equal(4, f.Ledger.ItemsDestroyedBy(InventoryReason.MailExpired));
        Assert.Equal((ushort)0, f.Transport.MailCountsFor(1)[^1].Open);

        f.Harness.SendMailClaim(1, 4, MailId, f.MailboxId);
        f.Harness.Ticks(1);
        Assert.Equal(MailReason.MailExpired, Assert.Single(f.Transport.MailResultsFor(1)).Reason);
    }

    [Fact]
    public void A_stand_alone_send_reloads_an_online_recipients_mailbox()
    {
        var f = new Fixture();
        f.Harness.Inbox.Post(GameServer.World.WorldCommand.MailArrived(f.Player.CharacterId));
        f.Harness.Ticks(1);

        Assert.Equal(f.Player.CharacterId, Assert.Single(f.Jobs.Requests).CharacterId);
    }

    [Fact]
    public void Two_arrivals_in_one_tick_ask_for_one_read_at_a_time()
    {
        var f = new Fixture();
        f.Harness.Inbox.Post(GameServer.World.WorldCommand.MailArrived(f.Player.CharacterId));
        f.Harness.Inbox.Post(GameServer.World.WorldCommand.MailArrived(f.Player.CharacterId));
        f.Harness.Ticks(2);

        Assert.Single(f.Jobs.Requests); // the worker's queue holds at most one read per character
    }

    [Fact]
    public void A_send_arriving_while_a_claim_waits_for_its_save_is_read_again_after_the_commit()
    {
        var f = new Fixture(open: 2);
        long id = f.Player.CharacterId;
        f.Harness.Saves.AutoComplete = false;
        f.Harness.SendMailClaim(1, 3, MailId, f.MailboxId); // claims one mail; its save waits
        f.Harness.Ticks(2);
        f.Harness.Inbox.Post(GameServer.World.WorldCommand.MailArrived(id)); // an admin compensation committed meanwhile
        f.Harness.Ticks(1);
        var first = Assert.Single(f.Jobs.Requests);
        long now = f.Harness.Clock.UtcNowMs;
        var compensation = new MailRow(99, id, MailKind.AdminCompensation, MailSubjects.AdminCompensation, [], 10, now, now + 1_000_000, []);
        var rows = f.Harness.World.Mail!.Mailbox(id).Append(compensation).ToList();
        f.Jobs.Reloaded.Enqueue(new MailReloaded(id, first.Version, new MailRecord(rows, rows.Count)));
        f.Harness.Ticks(1);

        // Refused while the claim waits (the read may predate it), and not asked again yet.
        Assert.DoesNotContain(f.Harness.World.Mail.Mailbox(id), m => m.MailId == 99);
        Assert.Single(f.Jobs.Requests);

        f.Harness.Saves.CompleteAll();
        f.Harness.Ticks(2);
        Assert.Equal(2, f.Jobs.Requests.Count); // asked again once the claim committed
        var after = f.Harness.World.Mail.Mailbox(id).Append(compensation).ToList();
        f.Jobs.Reloaded.Enqueue(new MailReloaded(id, f.Jobs.Requests[^1].Version, new MailRecord(after, after.Count)));
        f.Harness.Ticks(2);

        Assert.Contains(f.Harness.World.Mail.Mailbox(id), m => m.MailId == 99);
        Assert.Equal((ushort)2, f.Transport.MailCountsFor(1)[^1].Open); // the badge counts it
    }

    [Fact]
    public void A_failed_read_answers_from_memory_and_the_next_list_reads_again()
    {
        var f = new Fixture();
        long id = f.Player.CharacterId;
        f.Harness.Inbox.Post(GameServer.World.WorldCommand.MailArrived(id));
        f.Harness.Ticks(1);
        f.Jobs.Reloaded.Enqueue(new MailReloaded(id, f.Jobs.Requests[0].Version, null)); // the database failed
        f.Harness.Ticks(2);
        Assert.Single(f.Jobs.Requests); // no retry loop against a dead database

        f.Harness.SendMailOpen(1, 5); // every loaded mail is listed, but the box may be stale: read again
        f.Harness.Ticks(1);

        Assert.Equal(2, f.Jobs.Requests.Count);
        Assert.Empty(f.Transport.MailListsFor(1)); // the list waits for the read
        f.Jobs.Reloaded.Enqueue(new MailReloaded(id, f.Jobs.Requests[^1].Version, new MailRecord(f.Harness.World.Mail!.Mailbox(id), 1)));
        f.Harness.Ticks(1);
        Assert.Equal(5u, Assert.Single(f.Transport.MailListsFor(1)).RequestId);
    }

    [Fact]
    public void An_unclaimed_admin_compensation_that_expires_is_no_sink_and_a_settlement_mail_is()
    {
        var f = new Fixture();
        long id = f.Player.CharacterId;
        var ash = new MailItemRow(0, "m1.demonAsh", 0, 4, 0, false, false, null);
        f.Jobs.Expired.Enqueue(new MailExpiredRow(500, id, MailKind.AdminCompensation, 70, [ash]));
        f.Jobs.Expired.Enqueue(new MailExpiredRow(501, id, MailKind.ListingReturn, 0, [ash]));
        f.Harness.Ticks(2);

        Assert.Equal(2, f.Events.OfType<MailExpired>().Count()); // both published and in the telemetry
        Assert.Equal(0, f.Ledger.GoldDestroyedBy(InventoryReason.MailExpired)); // its gold was never created
        Assert.Equal(4, f.Ledger.ItemsDestroyedBy(InventoryReason.MailExpired)); // the returned listing's units only
    }

    // WP-M4-14 (spec 4.5 R-4.5-03, M4 decision 6a): the list is read anywhere, the attachments are taken at a mailbox.

    [Fact]
    public void A_claim_away_from_a_mailbox_is_refused_with_nothing_moved()
    {
        var f = new Fixture();
        long id = f.Player.CharacterId;
        long gold = f.Harness.World.Inventory!.Gold(id);
        f.Harness.Saves.AutoComplete = false;
        f.MoveTo(2_000, 0); // the field: no mailbox in reach

        f.Harness.SendMailOpen(1, 1); // the list and a mail are read anywhere
        f.Harness.SendMailOpen(1, 2, MailId);
        f.Harness.SendMailClaim(1, 3, MailId, f.MailboxId); // naming the city's mailbox from afar
        f.Harness.SendMailClaim(1, 4, 0, f.NpcId(FarMailbox)); // claim all at a mailbox out of reach (and sight)
        f.Harness.SendMailClaim(1, 5, MailId); // naming none
        f.Harness.Ticks(2);

        Assert.Equal(MailReason.None, Assert.Single(f.Transport.MailListsFor(1)).Reason);
        Assert.Equal(MailReason.None, Assert.Single(f.Transport.MailContentsFor(1)).Reason);
        Assert.Equal(new[] { MailReason.MailboxNotInReach, MailReason.MailboxNotInReach, MailReason.MailboxNotInReach },
            f.Transport.MailResultsFor(1).Select(r => r.Reason));
        Assert.DoesNotContain(f.Harness.Saves.Jobs, j => j.Mail is { Claims.Count: > 0 });
        Assert.Equal(gold, f.Harness.World.Inventory.Gold(id));
        Assert.Equal(4, f.Harness.World.Mail!.Find(id, MailId)!.Items.Single().Count);
        Assert.Empty(f.Events);
    }

    [Fact]
    public void A_claim_naming_another_npc_is_refused()
    {
        var f = new Fixture();
        f.MoveTo(200, 100); // the outpost and the mailbox both in reach

        f.Harness.SendMailClaim(1, 3, MailId, f.NpcId(Outpost));
        f.Harness.Ticks(2);

        Assert.Equal(MailReason.MailboxNotInReach, Assert.Single(f.Transport.MailResultsFor(1)).Reason);
        Assert.NotNull(f.Harness.World.Mail!.Find(f.Player.CharacterId, MailId));
    }

    [Fact]
    public void The_mailbox_opens_the_window_and_walking_away_before_the_claim_refuses_it()
    {
        var f = new Fixture();
        f.Harness.SendInteract(1, 1, f.MailboxId, NpcInteraction.Mail);
        f.Harness.SendInteract(1, 2, f.NpcId(Outpost), NpcInteraction.Mail); // the outpost offers no mail
        f.Harness.Ticks(1);
        Assert.Equal(new[] { (1u, OutpostReason.None), (2u, OutpostReason.InteractionUnavailable) },
            f.Transport.InteractResultsFor(1).Select(r => (r.RequestId, r.Reason)));

        f.MoveTo(0, 2_000); // walks away with the window open
        f.Harness.SendMailClaim(1, 3, MailId, f.MailboxId);
        f.Harness.Ticks(2);
        Assert.Equal(MailReason.MailboxNotInReach, Assert.Single(f.Transport.MailResultsFor(1)).Reason);

        f.MoveTo(0, 0); // back at the mailbox: the same mail is taken
        f.Harness.SendMailClaim(1, 4, MailId, f.MailboxId);
        f.Harness.Ticks(3);
        var taken = f.Transport.MailResultsFor(1)[^1];
        Assert.Equal((4u, MailReason.None, 25L), (taken.RequestId, taken.Reason, taken.GoldTaken));
    }

    [Fact]
    public void An_admin_compensation_is_claimed_at_a_mailbox_like_every_mail()
    {
        var f = new Fixture(open: 0);
        long id = f.Player.CharacterId;
        long now = f.Harness.Clock.UtcNowMs;
        var compensation = new MailRow(77, id, MailKind.AdminCompensation, MailSubjects.AdminCompensation, [], 10, now, now + 1_000_000, []);
        f.Harness.Inbox.Post(GameServer.World.WorldCommand.MailArrived(id)); // the admin panel wrote it (a stand-alone send)
        f.Harness.Ticks(1);
        f.Jobs.Reloaded.Enqueue(new MailReloaded(id, f.Jobs.Requests[^1].Version, new MailRecord([compensation], 1)));
        f.Harness.Ticks(2);
        Assert.NotNull(f.Harness.World.Mail!.Find(id, 77));
        f.MoveTo(2_000, 0);

        f.Harness.SendMailClaim(1, 3, 77, f.MailboxId);
        f.Harness.Ticks(2);
        Assert.Equal(MailReason.MailboxNotInReach, Assert.Single(f.Transport.MailResultsFor(1)).Reason);

        f.MoveTo(0, 0);
        f.Harness.SendMailClaim(1, 4, 77, f.MailboxId);
        f.Harness.Ticks(3);
        Assert.Equal((MailReason.None, 10L), (f.Transport.MailResultsFor(1)[^1].Reason, f.Transport.MailResultsFor(1)[^1].GoldTaken));
    }

    [Fact]
    public void A_world_with_npcs_and_mail_but_no_mailbox_is_reported()
    {
        // WP-M4-14: the attachments are taken only at a mailbox, so a settlement world without one could never be claimed.
        var tables = TestCombat.Tables();
        var withoutMailbox = TestMaps.Map(TestMaps.Open().Grid, [TestMaps.SpawnMarker(0, 0), EconomyFixture.Npc(Outpost, 200, 0, "m1.outpost")]);
        var withMailbox = TestMaps.Map(TestMaps.Open().Grid,
            [TestMaps.SpawnMarker(0, 0), EconomyFixture.Npc(Outpost, 200, 0, "m1.outpost"), EconomyFixture.Npc(Mailbox, 0, 900, "m1.mailbox")]);
        var noNpcs = TestMaps.Map(TestMaps.Open().Grid, [TestMaps.SpawnMarker(0, 0)]);

        Assert.Contains(GameServer.World.Map.WorldLoader.CheckConfigMarkers(withoutMailbox, tables),
            e => e.Code == GameServer.World.Map.WorldLoader.Codes.NoMailbox);
        Assert.DoesNotContain(GameServer.World.Map.WorldLoader.CheckConfigMarkers(withMailbox, tables),
            e => e.Code == GameServer.World.Map.WorldLoader.Codes.NoMailbox);
        Assert.DoesNotContain(GameServer.World.Map.WorldLoader.CheckConfigMarkers(noNpcs, tables),
            e => e.Code == GameServer.World.Map.WorldLoader.Codes.NoMailbox);
    }
}
