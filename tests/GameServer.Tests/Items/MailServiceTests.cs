using GameServer.Events;
using GameServer.Items;
using GameServer.Items.Mail;
using GameServer.Persistence;
using GameServer.Tests.Support;
using Shared.Data.Tables;
using Shared.Protocol;

namespace GameServer.Tests.Items;

/// <summary>
/// <see cref="MailService"/> on the world thread (spec 4.5 M4 mail, WP-M4-3), in memory: a claim takes the gold and what fits
/// through <see cref="InventoryService"/> as one operation, leaves the rest in the mail, refuses with the right reason, waits
/// for its save and answers once committed; a send rides the carrier's save and reaches an online recipient after the commit;
/// the cap gates new sources only. The database side is <c>MailPersistenceTests</c>.
/// </summary>
public class MailServiceTests
{
    private const long Alice = 1, Bob = 2;
    private static readonly long Day = 86_400_000;

    private readonly FakeClock _clock = new();
    private readonly ItemRules _items = ItemRules.From(TestCombat.Tables())!;
    private readonly MailRules _rules;
    private readonly InventoryService _inventory;
    private readonly MailService _mail;
    private readonly List<(long Character, SaveReason Reason)> _saves = [];
    private readonly List<long> _counts = [];
    private readonly List<CommittedMailClaim> _claims = [];
    private readonly List<MailSent> _sent = [];

    public MailServiceTests()
    {
        _rules = MailRules.From(TestCombat.Tables(), _items)!;
        _inventory = new InventoryService(_items, events: null);
        _mail = new MailService(_rules, _inventory, () => _clock.UtcNowMs)
        {
            RequestSave = (id, reason) => _saves.Add((id, reason)),
            CountChanged = id => _counts.Add(id),
            ClaimCommitted = (_, claim) => _claims.Add(claim),
            SendCommitted = sent => _sent.Add(sent),
        };
        foreach (var id in new[] { Alice, Bob })
        {
            Assert.True(_inventory.Attach(id, Array.Empty<ItemStackRow>()));
            _inventory.AttachGold(id, 0);
        }
    }

    private MailRow Mail(long mailId, long gold, params MailItemRow[] items) =>
        new(mailId, Alice, MailKind.ListingProceeds, MailSubjects.ListingProceeds, Array.Empty<MailArg>(), gold, _clock.UtcNowMs - 1_000,
            _clock.UtcNowMs + 30 * Day, items);

    private static MailItemRow Stack(int index, string defId, int count, bool stolen = false) =>
        new(index, defId, 0, count, 0, stolen, false, null);

    private void Attach(params MailRow[] mails) => _mail.Attach(Alice, new MailRecord(mails, mails.Length));

    private MailResult? Claim(uint requestId, long mailId, bool blocked = false, int connection = 1) =>
        _mail.Claim(Alice, new MailClaimAsk(requestId, mailId, blocked, connection, $"mail:test:1:{connection}:{requestId}"));

    /// <summary>The save the claim asked for: its inventory and mail batches, committed.</summary>
    private (InventoryBatch? Items, MailBatch? Mail) Commit(long characterId = Alice)
    {
        var saveId = Guid.NewGuid();
        var items = _inventory.TakeBatch(characterId, saveId);
        var mail = _mail.TakeBatch(characterId, saveId);
        _inventory.Committed(characterId, saveId);
        _mail.Committed(characterId, saveId);
        return (items, mail);
    }

    private List<Shared.Simulation.Items.ItemStack> Normal()
    {
        var items = _inventory.CurrentInventory(Alice);
        Assert.True(items.TryIndex((byte)_items.NormalBag.Code, out int index));
        var bag = items.BagAt(index);
        return Enumerable.Range(0, bag.SlotCount).Select(i => bag[i]).ToList();
    }

    private int Units(string defId) =>
        Normal().Where(s => !s.IsEmpty && _items.TryGetItem(s.ItemNetId, out var item) && item.Id == defId).Sum(s => s.Count);

    /// <summary>Fills the Normal bag with full demon horn stacks, leaving <paramref name="free"/> empty slots.</summary>
    private void FillBag(int free)
    {
        Assert.True(_items.TryGetItem("m1.demonHorn", out var horn));
        int slots = Normal().Count;
        _inventory.TryAdd(Alice, horn, 0, (slots - free) * horn.MaxStack, "fill", InventoryReason.Admin, "test");
    }

    [Fact]
    public void A_claim_takes_the_gold_and_the_items_as_one_operation_and_answers_only_after_the_commit()
    {
        Attach(Mail(10, 120, Stack(0, "m1.demonAsh", 5), Stack(1, "m1.alienCarapace", 2)));

        Assert.Null(Claim(1, 10)); // applied, waits for its save
        Assert.Equal((Alice, SaveReason.MailClaim), Assert.Single(_saves));
        Assert.Equal(120, _inventory.Gold(Alice));
        Assert.Equal(5, Units("m1.demonAsh"));
        Assert.Empty(_mail.Mailbox(Alice)); // emptied: gone from the box at once
        Assert.Empty(_claims);
        Assert.Null(Claim(1, 10)); // the same request while its save runs: still waiting

        var (items, mail) = Commit();

        var claim = Assert.Single(mail!.Claims);
        Assert.Equal((10L, 120L, 120L, true), (claim.MailId, claim.ExpectedGold, claim.GoldTaken, claim.Emptied));
        Assert.Equal(new[] { new MailItemTake(0, 5, 5), new MailItemTake(1, 2, 2) }, claim.Items);
        // One transaction: the bag's operation keys ride the same save as the mail rows (rule 5).
        Assert.Contains(items!.Ops, o => o.OpKey == claim.OpKey + ":g" && o.Reason == nameof(InventoryReason.MailClaim));
        Assert.Contains(items.Ops, o => o.OpKey == claim.OpKey + ":i0");
        var answer = Assert.Single(_claims).Answer;
        Assert.Equal((MailReason.None, (ushort)1, 120L, (ushort)0), (answer.Reason, answer.MailsEmptied, answer.GoldTaken, answer.ItemsLeft));
        Assert.Equal(7, answer.Taken.Sum(t => t.Count));
        Assert.Equal(MailReason.None, Claim(1, 10)!.Reason); // a repeated request gets its first answer
        Assert.Equal(MailReason.MailAlreadyClaimed, Claim(2, 10)!.Reason);
    }

    [Fact]
    public void A_claim_into_a_nearly_full_bag_takes_what_fits_and_leaves_the_rest_in_the_mail()
    {
        FillBag(free: 1);
        Attach(Mail(10, 30, Stack(0, "m1.demonAsh", 99), Stack(1, "m1.alienCarapace", 4)));

        Assert.Null(Claim(1, 10));
        Commit();

        var answer = Assert.Single(_claims).Answer;
        Assert.Equal((30L, (ushort)4, 0L, (ushort)0), (answer.GoldTaken, answer.ItemsLeft, answer.GoldLeft, answer.MailsEmptied));
        Assert.Equal(99, Units("m1.demonAsh"));
        var left = Assert.Single(_mail.Mailbox(Alice));
        Assert.Equal((0L, 4), (left.Gold, Assert.Single(left.Items).Count)); // the gold left the mail, the carapaces stay

        // Nothing fits now: refused at once, nothing changes.
        Assert.Equal(MailReason.BagFull, Claim(2, 10)!.Reason);
        Assert.Single(_mail.Mailbox(Alice));
    }

    [Fact]
    public void A_stack_takes_part_of_its_count_and_the_mail_keeps_the_rest()
    {
        Assert.True(_items.TryGetItem("m1.demonAsh", out var ash));
        FillBag(free: 1);
        Attach(Mail(10, 0, Stack(0, "m1.demonAsh", 99), Stack(1, "m1.demonAsh", 10)));

        Assert.Null(Claim(1, 10));
        var (_, mail) = Commit();

        var claim = Assert.Single(mail!.Claims);
        Assert.Equal(new[] { new MailItemTake(0, 99, 99) }, claim.Items);
        Assert.False(claim.Emptied);
        Assert.Equal(ash.MaxStack, Units("m1.demonAsh"));
        Assert.Equal(10, Assert.Single(Assert.Single(_mail.Mailbox(Alice)).Items).Count);
    }

    [Fact]
    public void Gold_alone_past_the_cap_is_refused_gold_cap_reached()
    {
        _inventory.MaxGold = 100;
        Attach(Mail(10, 150));

        Assert.Equal(MailReason.GoldCapReached, Claim(1, 10)!.Reason);
        Assert.Empty(_saves);
    }

    [Fact]
    public void A_claim_is_refused_in_combat_past_the_expiry_and_for_an_unknown_mail()
    {
        Attach(Mail(10, 5), Mail(11, 5) with { ExpiresUtcMs = _clock.UtcNowMs + 1_000 });

        Assert.Equal(MailReason.InCombat, Claim(1, 10, blocked: true)!.Reason);
        Assert.Equal(MailReason.MailNotFound, Claim(2, 99)!.Reason);
        _clock.Advance(1_000);
        Assert.Equal(MailReason.MailExpired, Claim(3, 11)!.Reason); // the world stops claims at the expiry time
        Assert.Single(_mail.Mailbox(Alice)); // an expired mail is not listed
        Assert.Equal(1, _mail.OpenCount(Alice)); // nor counted
        Assert.Empty(_saves);
    }

    [Fact]
    public void Claim_all_empties_the_open_mails_oldest_first_in_one_operation()
    {
        Attach(Mail(10, 1), Mail(11, 2, Stack(0, "m1.demonAsh", 3)), Mail(12, 4));

        Assert.Null(Claim(1, 0));
        var (_, mail) = Commit();

        Assert.Equal(new[] { 10L, 11L, 12L }, mail!.Claims.Select(c => c.MailId));
        Assert.Equal((ushort)3, _claims.Single().Answer.MailsEmptied);
        Assert.Equal(7, _inventory.Gold(Alice));
        Assert.Empty(_mail.Mailbox(Alice));
        Assert.Equal(0, _mail.OpenCount(Alice));
        Assert.Contains(Alice, _counts);
        Assert.Equal(MailReason.MailNotFound, Claim(2, 0)!.Reason); // nothing left to claim
    }

    [Fact]
    public void A_reused_request_id_past_the_remembered_answers_never_claims_twice()
    {
        Attach(Enumerable.Range(1, 20).Select(i => Mail(i, 1)).ToArray());
        for (uint request = 1; request <= 17; request++) // 17 committed answers push the first out of the remembered 16
        {
            Assert.Null(Claim(request, request));
            Commit();
        }

        Assert.Equal(MailReason.MailAlreadyClaimed, Claim(1, 18)!.Reason); // its key was used: nothing claimed twice
        Assert.Equal(17, _inventory.Gold(Alice));
    }

    [Fact]
    public void A_send_rides_the_carriers_save_and_reaches_an_online_recipient_after_the_commit()
    {
        Attach();
        _mail.Attach(Bob, MailRecord.Empty);
        var draft = new MailDraft(Alice, MailKind.ListingProceeds, MailSubjects.ListingProceeds,
            [new MailArg("item", MailArgKind.Item, 2)], [new MailAttachment("m1.demonAsh", 0, 3)], 40, "buy:r:7:1:1", "listing:7");

        Assert.Equal(MailSendStatus.Queued, _mail.Send(Bob, draft).Status);
        Assert.Equal(MailSendStatus.Repeated, _mail.Send(Bob, draft).Status);
        Assert.Equal(0, _mail.OpenCount(Alice)); // not before the commit

        var saveId = Guid.NewGuid();
        var batch = _mail.TakeBatch(Bob, saveId)!; // the carrier's save
        var send = Assert.Single(batch.Sends);
        Assert.Same(draft, send.Draft);
        Assert.Equal(send.CreatedUtcMs + 30 * Day, send.ExpiresUtcMs);
        send.MailId = 77; // the write's id
        _mail.Committed(Bob, saveId);

        var sent = Assert.Single(_sent);
        Assert.Equal((Alice, 77L, 40L, Bob), (sent.ToCharacterId, sent.MailId, sent.Gold, sent.CarrierCharacterId));
        Assert.Equal(1, _mail.OpenCount(Alice));
        Assert.Equal(77, Assert.Single(_mail.Mailbox(Alice)).MailId);
        Assert.Contains(Alice, _counts);
    }

    [Fact]
    public void A_send_committed_while_the_recipient_loads_is_merged_at_attach()
    {
        _mail.Attach(Bob, MailRecord.Empty);
        var draft = new MailDraft(Alice, MailKind.LootOverflow, MailSubjects.LootOverflow, [], [new MailAttachment("m1.demonAsh", 0, 1)],
            0, "loot:k:p1", "loot");
        _mail.Send(Bob, draft);
        var saveId = Guid.NewGuid();
        _mail.TakeBatch(Bob, saveId)!.Sends[0].MailId = 5;
        _mail.Committed(Bob, saveId); // Alice is not attached: remembered

        _mail.Attach(Alice, MailRecord.Empty); // her login read the mailbox before the send committed

        Assert.Equal(5, Assert.Single(_mail.Mailbox(Alice)).MailId);
        Assert.Equal(1, _mail.OpenCount(Alice));
    }

    [Fact]
    public void A_bad_draft_is_refused_and_a_carrier_must_be_on_the_world()
    {
        Attach();
        var good = new MailDraft(Bob, MailKind.ListingReturn, MailSubjects.ListingReturn, [], [new MailAttachment("m1.demonAsh", 0, 1)], 0,
            "k", "s");
        Assert.Equal(MailSendStatus.CarrierNotAttached, _mail.Send(Bob, good).Status);
        Assert.Equal(MailSendStatus.Invalid, _mail.Send(Alice, good with { Items = [], Gold = 0 }).Status);
        Assert.Equal(MailSendStatus.Invalid, _mail.Send(Alice, good with { Items = [new MailAttachment("nope", 0, 1)] }).Status);
        Assert.Equal(MailSendStatus.Invalid, _mail.Send(Alice, good with { SubjectId = "free text" }).Status);
        Assert.Equal(MailSendStatus.Invalid, _mail.Send(Alice, good with { Items = [new MailAttachment("m1.demonAsh", 0, 1000)] }).Status);
        Assert.Equal(MailSendStatus.Invalid,
            _mail.Send(Alice, good with { Items = Enumerable.Repeat(new MailAttachment("m1.demonAsh", 0, 1), 9).ToList() }).Status);
        Assert.Equal(MailSendStatus.Queued, _mail.Send(Alice, good).Status);
    }

    [Fact]
    public void The_cap_gates_new_sources_and_settlements_still_arrive()
    {
        int cap = _rules.Settings.MailboxCap;
        Attach(Enumerable.Range(1, cap - 1).Select(i => Mail(i, 1)).ToArray());
        Assert.True(_mail.HasRoom(Alice));

        _mail.Attach(Bob, MailRecord.Empty);
        for (int i = 0; i < 2; i++)
        {
            Assert.Equal(MailSendStatus.Queued, _mail.Send(Bob, new MailDraft(Alice, MailKind.ListingProceeds, MailSubjects.ListingProceeds,
                [], [], 10, $"buy:{i}", "listing")).Status);
        }
        var saveId = Guid.NewGuid();
        var batch = _mail.TakeBatch(Bob, saveId)!;
        for (int i = 0; i < batch.Sends.Count; i++)
            batch.Sends[i].MailId = 1_000 + i;
        _mail.Committed(Bob, saveId);

        Assert.Equal(cap + 1, _mail.OpenCount(Alice)); // over the cap: a settlement is never refused
        Assert.False(_mail.HasRoom(Alice));
    }

    [Fact]
    public void A_reload_read_before_a_change_is_not_applied()
    {
        Attach(Mail(10, 5));
        int version = _mail.Version(Alice);
        Assert.Null(Claim(1, 10)); // changes the box before the read comes back
        Assert.False(_mail.ApplyReload(Alice, new MailRecord([Mail(10, 5)], 1), version));
        Commit();
        Assert.False(_mail.ApplyReload(Alice, new MailRecord([Mail(10, 5)], 1), version)); // read before the commit

        Assert.True(_mail.ApplyReload(Alice, new MailRecord([Mail(20, 3)], 1), _mail.Version(Alice)));
        Assert.Equal(20, Assert.Single(_mail.Mailbox(Alice)).MailId);
    }

    [Fact]
    public void An_expired_mail_leaves_the_box_and_a_late_claim_hears_why()
    {
        Attach(Mail(10, 5));
        _mail.ApplyExpired(new MailExpiredRow(10, Alice, MailKind.ListingProceeds, 5, []));

        Assert.Equal(0, _mail.OpenCount(Alice));
        Assert.Equal(MailReason.MailExpired, Claim(1, 10)!.Reason);
    }

    [Fact]
    public void An_attachment_instance_keeps_its_id_flags_and_owner_when_claimed()
    {
        long sword = ItemInstanceIds.Make(Bob, 3);
        Attach(Mail(10, 0, new MailItemRow(0, "m2.trainingSword", 0, 1, sword, Stolen: true, NonTradable: false,
            ItemInstanceIds.NewProperties(Bob))));

        Assert.Null(Claim(1, 10));
        var (items, _) = Commit();

        var instance = Assert.Single(items!.Instances!, i => i.InstanceId == sword);
        Assert.True(instance.Stolen);
        Assert.Contains($"\"originalOwner\":{Bob}", instance.PropertiesJson, StringComparison.Ordinal);
        Assert.True(_inventory.TryGetProperties(Alice, sword, out var properties));
        Assert.Equal(Bob, properties.OriginalOwner);
    }
}
