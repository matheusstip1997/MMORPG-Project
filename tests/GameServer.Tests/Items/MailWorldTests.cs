using GameServer.Economy;
using GameServer.Entities;
using GameServer.Events;
using GameServer.Items;
using GameServer.Items.Mail;
using GameServer.Persistence;
using GameServer.Tests.Support;
using Shared.Protocol;

namespace GameServer.Tests.Items;

/// <summary>
/// Mail in the world (spec 4.5 M4 mail, 9.3; WP-M4-3) with the repository tables: the count of the M badge at enter world, the
/// list, a claim answered and published (<c>MailClaimed</c>) only after its save committed, the rate limit of spec 7.5, the
/// mail worker's reads and the expiry job's mails published as a sink counted by <see cref="EconomyLedger"/>.
/// </summary>
public class MailWorldTests
{
    private const long MailId = 40;

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
            Harness = new WorldHarness(events: new EventDispatchHook(Bus), bus: Bus, items: items, mail: Rules, mailJobs: Jobs);
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
        f.Harness.SendMailClaim(1, 3, MailId);
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

        f.Harness.SendMailClaim(1, 4, MailId);
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
        f.Harness.SendMailClaim(1, 3, MailId); // claims one mail; its save waits
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
}
