using GameServer.Contracts;
using GameServer.Crafting;
using GameServer.Economy;
using GameServer.Entities;
using GameServer.Events;
using GameServer.Items;
using GameServer.Items.Mail;
using GameServer.Persistence;
using GameServer.Progression;
using GameServer.Tests.Economy;
using GameServer.Tests.Market;
using GameServer.Tests.Support;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Contracts;
using Shared.Simulation.Crafting;
using Shared.Simulation.Items;
using Shared.Simulation.Jobs;

namespace GameServer.Tests.Contracts;

/// <summary>
/// Contracts in a running world (spec 5.4 R-5.4-01..04, M4 decisions 10 and 11, user answers 2026-11-15; WP-M4-10) with the
/// repository's tables: a proposal holds the customer's gold and materials in escrow in the customer's save, answered after the
/// commit; a crafter accepts at the board (the gate, the job, the caps); a forge session owned by the contract reads the
/// customer's units from the escrow, and its finish settles the contract in the crafter's save (the output by mail to the
/// customer, the gold less the contract fee to the crafter); the cancels and the expiry return everything to the customer by
/// mail; a lost session leaves everything in escrow; the refusals each have their reason; nothing is ever created (no faucet).
/// </summary>
[Trait("Category", "NoDuplication")]
public class ContractWorldTests
{
    private const string Board = "test.npc.contractBoard";
    private const string FarBoard = "test.npc.contractBoard.far";
    private const string Forge = "test.npc.forge";
    private const string FarForge = "test.npc.forge.far";
    private const string Bench = "test.npc.bench";
    private const string Sword = "m3.forgedSword", Stone = "m3.enchantStone";
    private const byte Normal = 1, Safe = 2, Equipped = 5;
    private const long CustomerGold = 1_000, CrafterGold = 100, ContractGold = 100;
    private const int Customer = 1, Crafter = 2, Other = 3;
    private static readonly long SwordId = ItemInstanceIds.Make(9, 1), MaxedSwordId = ItemInstanceIds.Make(9, 2),
        EquippedSwordId = ItemInstanceIds.Make(9, 3);

    /// <summary>A crafted sword's properties at <paramref name="level"/> (a crafter, a quality, a crafted stat: kept through a contract).</summary>
    private static string SwordProperties(int level)
    {
        var sword = ItemProperties.New(1);
        sword.CrafterId = 77;
        sword.Quality = 300;
        sword.Enchant = level;
        sword.CraftedStats.Add(new ItemStatBonus("atk", 20, 0));
        return sword.ToJson();
    }

    [Fact]
    public void A_proposal_holds_the_gold_and_materials_in_escrow_and_is_answered_after_the_commit()
    {
        var f = new Fixture();
        f.Harness.Saves.AutoComplete = false;
        f.Propose(1, ContractGold);
        f.Harness.Ticks(2); // the customer's save is issued, not completed

        Assert.Empty(f.Transport.ContractResultsFor(Customer));
        var save = f.Harness.Saves.Jobs.Single(j => j.Contract is { Proposals.Count: > 0 });
        var row = Assert.Single(save.Contract!.Proposals);
        var hold = Assert.Single(save.Escrow!.Holds);
        Assert.Equal((row.EscrowId, ContractGold, 2), (hold.EscrowId, hold.Gold, hold.Items.Sum(i => i.Count))); // one transaction (rule 5)
        Assert.Equal(EscrowHolderKind.Contract, hold.HolderKind);

        f.Harness.Saves.CompleteAll();
        f.Harness.Ticks(2);

        var answer = Assert.Single(f.Transport.ContractResultsFor(Customer));
        Assert.Equal((ContractAction.Propose, ContractReason.None, row.ContractId), (answer.Action, answer.Reason, answer.ContractId));
        Assert.Equal(CustomerGold - ContractGold, f.Inventory.CommittedGold(f.CustomerId));
        Assert.Equal(9, f.Units(f.CustomerId, EconomyFixture.Ash)); // 10 + the stolen one, less the two held
        Assert.Equal(ContractStatus.Proposed, f.Contracts.StatusOf(row.ContractId));
        Assert.Contains(f.Log.Events, e => e is ContractProposed p && p.ContractId == row.ContractId && p.UnitsHeld == 2);
        Assert.Equal(1, f.Transport.ContractListsFor(Customer)[^1].Badge);
    }

    [Fact]
    public void The_full_sword_contract_goes_to_the_customer_by_mail_and_pays_the_crafter_less_the_fee()
    {
        var f = new Fixture();
        long id = f.ProposeCommitted(ContractGold);
        Assert.Equal(ContractReason.None, f.Accept(10, id).Reason);
        Assert.Equal(ContractStatus.Accepted, f.Contracts.StatusOf(id));

        Assert.Equal(ForgeReason.None, f.StartContract(20, id).Reason);
        Assert.Equal(ContractStatus.InProgress, f.Contracts.StatusOf(id));
        f.Harness.SendForgeFinish(Crafter, 21);
        f.Harness.Ticks(4);

        var result = f.Transport.ForgeResultsFor(Crafter)[^1];
        Assert.Equal((ForgeReason.None, true, 0L), (result.Reason, result.Success, result.InstanceId)); // the sword went by mail
        Assert.Equal(ContractStatus.None, f.Contracts.StatusOf(id)); // completed: closed
        var save = f.Harness.Saves.Jobs.Last(j => j.Contract is { Writes.Count: > 0 } && j.CharacterId == f.CrafterId
            && j.Contract.Writes.Any(w => w.Kind == ContractWriteKind.Complete));
        var output = save.Mail!.Sends.Single(s => s.Draft.Kind == MailKind.ContractOutput).Draft;
        Assert.Equal(f.CustomerId, output.ToCharacterId);
        var sword = Assert.Single(output.Items);
        Assert.Equal(("m3.forgedSword", 1, 0L), (sword.DefId, sword.Count, sword.InstanceId));
        Assert.True(ItemProperties.TryParse(sword.PropertiesJson!, out var properties, out _));
        Assert.Equal(f.CrafterId, properties.CrafterId); // the crafter made it; the customer holds it (R-5.4-02)
        Assert.Equal(EscrowTargetKind.Consumed, Assert.Single(save.Escrow!.Releases).ToKind);
        Assert.Single(save.Craft!.Ops, o => o.ContractId == id && o.RecipientId == f.CustomerId);
        Assert.Contains(save.Contract!.Counters, c => c.Outcome == PortfolioOutcome.ContractCompleted && c.CharacterId == f.CrafterId);

        const long fee = 5; // 5 % of 100 (FeeRates contract)
        Assert.Equal(CrafterGold + ContractGold - fee, f.Inventory.CommittedGold(f.CrafterId));
        Assert.Equal(2, f.Units(f.CrafterId, EconomyFixture.Ash)); // the crafter's own ash untouched: it never held the customer's
        var completed = Assert.IsType<ContractCompleted>(Assert.Single(f.Log.Events, e => e is ContractCompleted));
        Assert.Equal((ContractGold, fee, true), (completed.Gold, completed.Fee, completed.Success));
        Assert.Equal(fee, f.Ledger.GoldDestroyedBy(InventoryReason.Fee));
        Assert.Equal(0, f.Ledger.GoldCreated); // no faucet (R-4.5-02)
        Assert.Contains(f.Log.Events, e => e is Crafted c && c.CharacterId == f.CrafterId && c.RecipientCharacterId == f.CustomerId);
    }

    [Fact]
    public void Each_refusal_has_its_reason_and_changes_nothing()
    {
        var f = new Fixture();
        f.Propose(1, 5); // below the minimum value
        f.Propose(2, ContractGold, deadlineHours: 0);
        f.Propose(3, ContractGold, deadlineHours: 73);
        f.Propose(4, ContractGold, slots: [f.CustomerSlot(0)]); // one slot short
        f.Propose(5, ContractGold, slots: [f.CustomerSlot(1), f.CustomerSlot(0)]); // a sell-only horn
        f.Propose(6, ContractGold, slots: [f.CustomerSlot(2), f.CustomerSlot(0)]); // a stolen ash
        f.Propose(7, CustomerGold + 1);
        f.Harness.Ticks(1);

        Assert.Equal(new[]
        {
            ContractReason.ContractValueTooLow, ContractReason.ContractDeadlineInvalid, ContractReason.ContractDeadlineInvalid,
            ContractReason.ContractTermsInvalid, ContractReason.ContractTermsInvalid, ContractReason.ContractNotTradable,
            ContractReason.NotEnoughGold,
        }, f.Transport.ContractResultsFor(Customer).Select(r => r.Reason));
        Assert.Equal(CustomerGold, f.Inventory.Gold(f.CustomerId));
        Assert.Equal(0, f.Contracts.OpenCount);

        long id = f.ProposeCommitted(ContractGold);
        Assert.Equal(ContractReason.ContractOwn, f.Accept(30, id, Customer).Reason);
        Assert.Equal(ContractReason.OutOfRange, f.Accept(31, id, Crafter, FarBoard).Reason);
        Assert.Equal(ContractReason.ContractNotFound, f.Accept(32, 999).Reason);
        Assert.Equal(ContractReason.ContractJobRequired, f.Accept(33, id, Other).Reason); // no Blacksmith
        Assert.Equal(ContractStatus.Proposed, f.Contracts.StatusOf(id));
    }

    [Fact]
    public void The_per_pair_cap_counts_acceptances_of_the_pair_per_utc_day()
    {
        var f = new Fixture(pairCap: 1);
        long first = f.ProposeCommitted(ContractGold);
        Assert.Equal(ContractReason.None, f.Accept(10, first).Reason);
        long second = f.ProposeCommitted(ContractGold, request: 101);
        Assert.Equal(ContractReason.ContractPairCap, f.Accept(11, second).Reason); // R-5.4-04 anti-farming (the day and the pair: ContractMathTests)
        Assert.Equal(ContractStatus.Proposed, f.Contracts.StatusOf(second));
    }

    [Fact]
    public void The_pair_counts_of_an_earlier_utc_day_are_dropped_and_the_cap_counts_afresh()
    {
        var f = new Fixture(pairCap: 1, msBeforeMidnight: 5_000);
        long first = f.ProposeCommitted(ContractGold);
        Assert.Equal(ContractReason.None, f.Accept(10, first).Reason);
        Assert.Equal(1, f.Contracts.PairCountsHeld);
        long second = f.ProposeCommitted(ContractGold, request: 101);
        Assert.Equal(ContractReason.ContractPairCap, f.Accept(11, second).Reason); // still today

        long now = f.Harness.Clock.UtcNowMs;
        f.Harness.Clock.Advance((ContractMath.UtcDay(now) + 1) * ContractMath.MsPerDay - now + 1); // just past the next UTC midnight
        f.Harness.Ticks(1);
        Assert.Equal(0, f.Contracts.PairCountsHeld); // review of PR #261: yesterday's count is gone (phase 7)

        Assert.Equal(ContractReason.None, f.Accept(12, second).Reason); // the cap of 1 counts today's acceptances only
        Assert.Equal(1, f.Contracts.PairCountsHeld);
    }

    [Fact]
    public void The_board_pages_after_its_cursor_in_id_order_with_the_filters()
    {
        var f = new Fixture();
        f.Settings.BoardPageSize = 1;
        long a = f.ProposeCommitted(ContractGold, request: 101);
        long b = f.ProposeCommitted(ContractGold, request: 102);
        long c = f.ProposeCommitted(ContractGold, request: 103);
        long d = f.ProposeCommitted(ContractGold, request: 104);
        Assert.Equal(ContractReason.None, f.Accept(10, b).Reason); // accepted: off the board

        f.Harness.SendContractBoard(Crafter, new ContractBoardRequest
        {
            RequestId = 60, NpcEntityId = f.NpcId(Board), AfterContractId = a, Kind = ForgeKind.Mold, JobNetId = 1,
        });
        f.Harness.SendContractBoard(Crafter, new ContractBoardRequest { RequestId = 61, NpcEntityId = f.NpcId(Board), AfterContractId = c });
        f.Harness.SendContractBoard(Crafter, new ContractBoardRequest { RequestId = 62, NpcEntityId = f.NpcId(Board), Kind = ForgeKind.Recipe });
        f.Harness.SendContractBoard(Crafter, new ContractBoardRequest { RequestId = 63, NpcEntityId = f.NpcId(Board), JobNetId = 2 });
        f.Harness.Ticks(1);

        var pages = f.Transport.ContractBoardsFor(Crafter);
        Assert.Equal((60u, true), (pages[0].RequestId, pages[0].HasMore));
        Assert.Equal(new[] { c }, pages[0].Rows.Select(r => r.ContractId));
        Assert.Equal((61u, false), (pages[1].RequestId, pages[1].HasMore));
        Assert.Equal(new[] { d }, pages[1].Rows.Select(r => r.ContractId));
        Assert.Empty(pages[2].Rows); // no recipe contracts
        Assert.Empty(pages[3].Rows); // no Jeweler contracts

        Assert.Equal(new[] { a, c, d }, f.Contracts.Board(0, ForgeKind.None, 0, 10, _ => 1).Select(r => r.ContractId));
        Assert.Equal(new[] { c }, f.Contracts.Board(1, ForgeKind.Mold, b, 1, _ => 1).Select(r => r.ContractId));
        Assert.Empty(f.Contracts.Board(0, ForgeKind.None, d, 10, _ => 1));
        Assert.Empty(f.Contracts.Board(0, ForgeKind.None, long.MaxValue, 10, _ => 1));
        Assert.Empty(f.Contracts.Board(0, ForgeKind.None, 0, 0, _ => 1));
    }

    [Fact]
    public void A_crafter_cancel_whose_return_mail_the_rules_refuse_is_refused_and_moves_nothing()
    {
        var f = new Fixture();
        long id = f.ProposeCommitted(ContractGold);
        Assert.Equal(ContractReason.None, f.Accept(10, id).Reason);
        Assert.Equal(ForgeReason.None, f.StartContract(20, id).Reason);
        f.Harness.SendForgeCancel(Crafter, 21);
        f.Harness.Ticks(2); // work started: the cancel would cost 10
        int attachments = f.MailSettings.AttachmentsPerMail;
        f.MailSettings.AttachmentsPerMail = 0; // the escrow's ash no longer fits one return mail (review of PR #261)

        f.Harness.SendContract(Crafter, new ContractRequest { RequestId = 50, Action = ContractAction.Cancel, ContractId = id });
        f.Harness.Ticks(4);

        var answer = f.Transport.ContractResultsFor(Crafter)[^1];
        Assert.Equal((50u, ContractReason.ContractTermsInvalid), (answer.RequestId, answer.Reason));
        Assert.Equal(ContractStatus.Accepted, f.Contracts.StatusOf(id));
        Assert.True(f.Harness.World.Escrow!.IsHeld(f.Contracts.Find(id)!.EscrowId));
        Assert.Equal((CrafterGold, CrafterGold), (f.Inventory.Gold(f.CrafterId), f.Inventory.CommittedGold(f.CrafterId)));
        Assert.Equal(0, f.Ledger.GoldDestroyedBy(InventoryReason.ContractCancel));
        Assert.Empty(f.Log.Events.OfType<ContractCancelled>());
        Assert.DoesNotContain(f.Harness.Saves.Jobs, j => j.Contract != null && j.Contract.Writes.Any(w => w.Kind == ContractWriteKind.CrafterCancel));

        f.MailSettings.AttachmentsPerMail = attachments;
        f.Harness.SendContract(Crafter, new ContractRequest { RequestId = 51, Action = ContractAction.Cancel, ContractId = id });
        f.Harness.Ticks(4);
        Assert.Equal((51u, ContractReason.None, 10L), (f.Transport.ContractResultsFor(Crafter)[^1].RequestId,
            f.Transport.ContractResultsFor(Crafter)[^1].Reason, f.Transport.ContractResultsFor(Crafter)[^1].Cost));
        Assert.Equal(ContractStatus.None, f.Contracts.StatusOf(id));
    }

    [Fact]
    public void A_finish_whose_output_mail_the_rules_refuse_is_refused_and_the_session_runs_on()
    {
        var f = new Fixture();
        long id = f.ProposeCommitted(ContractGold);
        Assert.Equal(ContractReason.None, f.Accept(10, id).Reason);
        Assert.Equal(ForgeReason.None, f.StartContract(20, id).Reason);
        int attachments = f.MailSettings.AttachmentsPerMail;
        f.MailSettings.AttachmentsPerMail = 0; // the sword no longer fits the customer's output mail (review of PR #261)

        f.Harness.SendForgeFinish(Crafter, 21);
        f.Harness.Ticks(4);

        var refused = f.Transport.ForgeResultsFor(Crafter)[^1];
        Assert.Equal((21u, ForgeReason.ContractStateInvalid), (refused.RequestId, refused.Reason));
        Assert.Equal(ContractStatus.InProgress, f.Contracts.StatusOf(id));
        Assert.NotNull(f.Harness.World.Forge!.Session(f.CrafterId));
        Assert.True(f.Harness.World.Escrow!.IsHeld(f.Contracts.Find(id)!.EscrowId));
        Assert.Equal(CrafterGold, f.Inventory.Gold(f.CrafterId));
        Assert.Equal(0, f.Ledger.GoldDestroyedBy(InventoryReason.Fee));
        Assert.Empty(f.Log.Events.OfType<ContractCompleted>());

        f.MailSettings.AttachmentsPerMail = attachments;
        f.Harness.SendForgeFinish(Crafter, 22);
        f.Harness.Ticks(4);
        var finished = f.Transport.ForgeResultsFor(Crafter)[^1];
        Assert.Equal((22u, ForgeReason.None, true), (finished.RequestId, finished.Reason, finished.Success));
        Assert.Equal(ContractStatus.None, f.Contracts.StatusOf(id));
        Assert.Single(f.Log.Events.OfType<ContractCompleted>());
    }

    [Fact]
    public void The_customer_cancels_for_free_before_work_started_by_mail_and_not_after()
    {
        var f = new Fixture();
        long id = f.ProposeCommitted(ContractGold);
        var release = new TaskCompletionSource();
        f.Releaser.Hold = release.Task; // the release to mail stays in flight: the answer must wait for it (review of PR #261)
        f.Harness.SendContract(Customer, new ContractRequest { RequestId = 40, Action = ContractAction.Cancel, ContractId = id });
        f.Harness.Ticks(3);
        Assert.DoesNotContain(f.Transport.ContractResultsFor(Customer), r => r.RequestId == 40);
        Assert.Equal(ContractStatus.Cancelled, f.Contracts.StatusOf(id)); // closing
        release.SetResult();
        f.TickUntil(() => f.Contracts.StatusOf(id) == ContractStatus.None);
        f.Releaser.Hold = null;

        var call = Assert.Single(f.Releaser.Calls);
        Assert.Equal((f.CustomerId, (short)MailKind.ContractReturn, (short)ContractDbState.Cancelled),
            (call.To, call.Kind, call.Close!.State));
        Assert.Equal(ContractReason.None, f.Transport.ContractResultsFor(Customer)[^1].Reason);
        Assert.Contains(f.Log.Events, e => e is ContractCancelled { ByCrafter: false, Cost: 0 });

        long started = f.ProposeCommitted(ContractGold, request: 101);
        Assert.Equal(ContractReason.None, f.Accept(10, started).Reason);
        Assert.Equal(ForgeReason.None, f.StartContract(20, started).Reason);
        f.Harness.SendForgeCancel(Crafter, 21);
        f.Harness.Ticks(2);
        Assert.Equal(ContractStatus.Accepted, f.Contracts.StatusOf(started)); // the session ended; work started stays

        f.Harness.SendContract(Customer, new ContractRequest { RequestId = 41, Action = ContractAction.Cancel, ContractId = started });
        f.Harness.Ticks(1);
        Assert.Equal(ContractReason.ContractStateInvalid, f.Transport.ContractResultsFor(Customer)[^1].Reason);
    }

    [Fact]
    public void The_crafter_cancel_after_work_started_costs_ten_percent_and_returns_everything_in_one_save()
    {
        var f = new Fixture();
        long id = f.ProposeCommitted(ContractGold);
        Assert.Equal(ContractReason.None, f.Accept(10, id).Reason);
        Assert.Equal(ForgeReason.None, f.StartContract(20, id).Reason);
        f.Harness.SendForgeCancel(Crafter, 21);
        f.Harness.Ticks(2);

        f.Harness.SendContract(Crafter, new ContractRequest { RequestId = 50, Action = ContractAction.Cancel, ContractId = id });
        f.Harness.Ticks(4);

        var answer = f.Transport.ContractResultsFor(Crafter)[^1];
        Assert.Equal((ContractReason.None, 10L), (answer.Reason, answer.Cost));
        Assert.Equal(CrafterGold - 10, f.Inventory.CommittedGold(f.CrafterId));
        var save = f.Harness.Saves.Jobs.Last(j => j.CharacterId == f.CrafterId && j.Contract is { Writes.Count: > 0 });
        Assert.Contains(save.Contract!.Writes, w => w.Kind == ContractWriteKind.CrafterCancel && w.Cost == 10);
        var back = save.Mail!.Sends.Single(s => s.Draft.Kind == MailKind.ContractReturn).Draft;
        Assert.Equal((f.CustomerId, ContractGold, 2), (back.ToCharacterId, back.Gold, back.Items.Sum(i => i.Count)));
        Assert.Equal(10, f.Ledger.GoldDestroyedBy(InventoryReason.ContractCancel));
        Assert.Equal(ContractStatus.None, f.Contracts.StatusOf(id));
    }

    [Fact]
    public void A_forge_session_lost_with_its_crafter_leaves_everything_in_escrow_and_the_contract_accepted()
    {
        var f = new Fixture();
        long id = f.ProposeCommitted(ContractGold);
        Assert.Equal(ContractReason.None, f.Accept(10, id).Reason);
        Assert.Equal(ForgeReason.None, f.StartContract(20, id).Reason);

        f.Harness.Leave(Crafter);
        f.Harness.Ticks(3);

        Assert.Equal(ContractStatus.Accepted, f.Contracts.StatusOf(id)); // the deadline runs on
        Assert.True(f.Harness.World.Escrow!.IsHeld(f.Contracts.Find(id)!.EscrowId));
        Assert.Empty(f.Log.Events.OfType<ContractCompleted>());
    }

    [Fact]
    public void A_complete_racing_a_cancel_settles_once()
    {
        var f = new Fixture();
        long id = f.ProposeCommitted(ContractGold);
        Assert.Equal(ContractReason.None, f.Accept(10, id).Reason);
        Assert.Equal(ForgeReason.None, f.StartContract(20, id).Reason);
        f.Harness.Saves.AutoComplete = false;

        // All in one tick: the customer (the lower entity) asks first and finds work started; the crafter's finish settles, and its
        // cancel right after finds the contract settling.
        f.Harness.SendForgeFinish(Crafter, 21);
        f.Harness.SendContract(Crafter, new ContractRequest { RequestId = 51, Action = ContractAction.Cancel, ContractId = id });
        f.Harness.SendContract(Customer, new ContractRequest { RequestId = 52, Action = ContractAction.Cancel, ContractId = id });
        f.Harness.Ticks(1);

        Assert.Contains(f.Transport.ContractResultsFor(Crafter).Select(r => r.Reason), r => r is ContractReason.ContractBusy or ContractReason.ContractStateInvalid);
        Assert.Equal(ContractReason.ContractStateInvalid, f.Transport.ContractResultsFor(Customer)[^1].Reason);
        f.Harness.Saves.AutoComplete = true; // the work-started save and then the completion's commit
        f.Harness.Saves.CompleteAll();
        f.Harness.Ticks(4);
        Assert.True(f.Log.Events.OfType<ContractCompleted>().Count() == 1, string.Join(",", f.Transport.ForgeResultsFor(Crafter).Select(r => r.Reason))
            + " / " + string.Join(",", f.Transport.ContractResultsFor(Crafter).Select(r => r.Reason)) + " / " + f.Contracts.StatusOf(id));
        Assert.Empty(f.Log.Events.OfType<ContractCancelled>());
        Assert.Empty(f.Releaser.Calls);
    }

    [Fact]
    public void An_unaccepted_proposal_expires_and_returns_by_mail()
    {
        var f = new Fixture();
        long id = f.ProposeCommitted(ContractGold);
        f.Harness.Clock.Advance(f.Settings.ProposalOpenHours * Shared.Simulation.Contracts.ContractMath.MsPerHour);

        f.TickUntil(() => f.Contracts.StatusOf(id) == ContractStatus.None);

        var call = Assert.Single(f.Releaser.Calls);
        Assert.Equal((f.CustomerId, (short)MailKind.ContractReturn, (short)ContractDbState.Expired), (call.To, call.Kind, call.Close!.State));
        Assert.Contains(f.Log.Events, e => e is ContractExpired x && x.ContractId == id && x.CrafterId == 0);
    }

    [Fact]
    public void A_deadline_passing_during_a_session_ends_it_and_returns_everything()
    {
        var f = new Fixture();
        long id = f.ProposeCommitted(ContractGold);
        Assert.Equal(ContractReason.None, f.Accept(10, id).Reason);
        Assert.Equal(ForgeReason.None, f.StartContract(20, id).Reason);
        f.CommittedCharge(id, 10); // the database's expiry wrote the charge (its work_started committed)
        Assert.Equal(1, f.Contracts.ExpireNow(f.CrafterId, f.Harness.Clock.UtcNowMs)); // the deadline comes now (the dev console's hook)

        f.TickUntil(() => f.Contracts.StatusOf(id) == ContractStatus.None);

        Assert.True(f.Transport.ForgeStatesFor(Crafter).Any(s => s.Reason == ForgeReason.ContractExpired && s.Phase == ForgePhase.Cancelled),
            string.Join(",", f.Transport.ForgeStatesFor(Crafter).Select(s => $"{s.RequestId}:{s.Reason}:{s.Phase}")));
        Assert.Null(f.Harness.World.Forge!.Session(f.CrafterId));

        // "Yes, charge on expiry" (user, 2026-11-16): the work started, so the online crafter pays 10 % of the gold in its own save.
        f.TickUntil(() => f.Log.Events.OfType<ContractCharged>().Any());
        Assert.Contains(f.Log.Events, e => e is ContractExpired x && x.ContractId == id && x.CrafterId == f.CrafterId && x.Charged == 10);
        f.Harness.Ticks(2);
        var charged = Assert.Single(f.Log.Events.OfType<ContractCharged>());
        Assert.Equal((id, 10L, 10L), (charged.ContractId, charged.Amount, charged.Taken));
        Assert.Equal(CrafterGold - 10, f.Inventory.CommittedGold(f.CrafterId));
        Assert.Equal(10, f.Ledger.GoldDestroyedBy(InventoryReason.ContractCancel));
    }

    [Fact]
    public void An_unstarted_contract_expires_without_a_charge()
    {
        var f = new Fixture();
        long id = f.ProposeCommitted(ContractGold);
        Assert.Equal(ContractReason.None, f.Accept(10, id).Reason);
        Assert.Equal(1, f.Contracts.ExpireNow(f.CrafterId, f.Harness.Clock.UtcNowMs));

        f.TickUntil(() => f.Log.Events.OfType<ContractExpired>().Any(x => x.ContractId == id));
        f.Harness.Ticks(3);

        Assert.Contains(f.Log.Events, e => e is ContractExpired x && x.ContractId == id && x.Charged == 0);
        Assert.Empty(f.Log.Events.OfType<ContractCharged>());
        Assert.Equal(CrafterGold, f.Inventory.CommittedGold(f.CrafterId));
    }

    [Fact]
    public void A_started_contract_whose_expiry_wrote_no_charge_costs_nothing()
    {
        // Review of PR #263: the world saw the first forge start, the database's expiry did not (its work_started write not
        // committed yet), so it wrote no charge. The committed row decides: nothing is collected and the crafter's saves go on.
        var f = new Fixture();
        long id = f.ProposeCommitted(ContractGold);
        Assert.Equal(ContractReason.None, f.Accept(10, id).Reason);
        Assert.Equal(ForgeReason.None, f.StartContract(20, id).Reason);
        Assert.True(f.Contracts.Find(id)!.WorkStarted);
        Assert.Equal(1, f.Contracts.ExpireNow(f.CrafterId, f.Harness.Clock.UtcNowMs));

        f.TickUntil(() => f.Log.Events.OfType<ContractExpired>().Any(x => x.ContractId == id));
        f.Harness.Ticks(3);

        Assert.Contains(f.Log.Events, e => e is ContractExpired x && x.ContractId == id && x.Charged == 0);
        Assert.Empty(f.Log.Events.OfType<ContractCharged>());
        Assert.Equal(0, f.Contracts.ChargesWaiting);
        Assert.Equal(CrafterGold, f.Inventory.CommittedGold(f.CrafterId));
    }

    [Fact]
    public void The_board_lists_open_proposals_and_a_portfolio_view_without_a_reader_is_refused()
    {
        var f = new Fixture();
        long id = f.ProposeCommitted(ContractGold);
        f.Harness.SendContractBoard(Crafter, new ContractBoardRequest { RequestId = 60, NpcEntityId = f.NpcId(Board) });
        f.Harness.SendContractBoard(Crafter, new ContractBoardRequest { RequestId = 61, NpcEntityId = f.NpcId(FarBoard) });
        f.Harness.SendPortfolio(Crafter, new PortfolioRequest { RequestId = 62, Action = PortfolioAction.View });
        f.Harness.Ticks(1);

        var pages = f.Transport.ContractBoardsFor(Crafter);
        var row = Assert.Single(pages[0].Rows);
        Assert.Equal((id, ContractStatus.Proposed, ContractRole.None, 2), (row.ContractId, row.Status, row.Role, row.Slots.Length));
        Assert.Equal(ContractReason.OutOfRange, pages[1].Reason);
        Assert.Equal(ContractReason.PortfolioNotFound, f.Transport.PortfoliosFor(Crafter)[^1].Reason);
    }

    // ---- Enchant contracts (WP-M4-10 part C; user answers 2026-11-16: one attempt, the stones' supplier in the terms) ----

    [Fact]
    public void An_enchant_proposal_holds_the_sword_instance_and_the_customer_s_stones_in_one_escrow()
    {
        var f = new Fixture();
        f.Harness.Saves.AutoComplete = false;
        f.ProposeEnchant(1, [f.EnchantItem(), f.EnchantStones(ContractSupplier.Customer)]);
        f.Harness.Ticks(2);

        var save = f.Harness.Saves.Jobs.Single(j => j.Contract is { Proposals.Count: > 0 });
        var row = Assert.Single(save.Contract!.Proposals);
        Assert.Equal((ForgeKind.Enchant, Sword, "blacksmith"), (row.Kind, row.DefId, row.JobId));
        Assert.Equal((SwordId, 0), (row.Slots[0].InstanceId, row.Slots[0].EnchantLevel));
        var hold = Assert.Single(save.Escrow!.Holds);
        var sword = Assert.Single(hold.Items, i => i.InstanceId != 0);
        Assert.Equal((SwordId, Sword, 1), (sword.InstanceId, sword.DefId, sword.Count));
        Assert.True(ItemProperties.TryParse(sword.PropertiesJson!, out var properties, out _));
        Assert.Equal((0, 77L, 300), (properties.Enchant, properties.CrafterId, properties.Quality)); // the instance as it is
        Assert.Equal(1, hold.Items.Where(i => i.DefId == Stone).Sum(i => i.Count));
        Assert.Equal(ContractGold, hold.Gold);

        f.Harness.Saves.CompleteAll();
        f.Harness.Ticks(2);
        var answer = Assert.Single(f.Transport.ContractResultsFor(Customer));
        Assert.Equal(ContractReason.None, answer.Reason);
        Assert.True(f.Inventory.CurrentInventory(f.CustomerId).At(new InventoryLocation(Safe, 0)).IsEmpty); // the sword is in escrow
        Assert.Equal(1, f.Units(f.CustomerId, Stone));
        var entry = f.Transport.ContractListsFor(Customer)[^1].Rows.Single();
        Assert.Equal((ForgeKind.Enchant, SwordId, ContractSupplier.Customer), (entry.Kind, entry.Slots[0].InstanceId, entry.Slots[1].Supplier));
    }

    [Fact]
    public void An_enchant_proposal_with_the_crafter_s_stones_holds_the_sword_only()
    {
        var f = new Fixture();
        long id = f.EnchantAccepted(ContractSupplier.Crafter);

        var escrowed = f.Escrowed(id);
        Assert.Equal(SwordId, Assert.Single(escrowed.Items).InstanceId);
        Assert.Equal(2, f.Units(f.CustomerId, Stone)); // the customer's stones stay in its bag
    }

    [Fact]
    public void Enchant_proposals_of_an_equipped_or_maxed_or_wrong_item_or_stone_count_are_refused_and_hold_nothing()
    {
        var f = new Fixture();
        var stones = f.EnchantStones(ContractSupplier.Customer);
        f.ProposeEnchant(1, [f.EnchantItem(Equipped, 0, EquippedSwordId), stones]); // equipped: never escrowed
        f.ProposeEnchant(2, [f.EnchantItem(Safe, 1, MaxedSwordId), stones]); // +9
        f.ProposeEnchant(3, [f.EnchantItem(), f.EnchantStones(ContractSupplier.Customer, count: 2)]); // the tier takes one stone
        f.ProposeEnchant(4, [f.EnchantItem(Normal, 0, 1, EconomyFixture.Ash), stones], EconomyFixture.Ash); // not equipment
        f.ProposeEnchant(5, [f.EnchantItem(Safe, 0, MaxedSwordId), stones]); // another instance than the slot holds
        f.ProposeEnchant(6, [f.EnchantItem()]); // no stones slot
        f.Harness.Ticks(2);

        Assert.Equal(new[]
        {
            ContractReason.ContractTermsInvalid, ContractReason.ContractTermsInvalid, ContractReason.ContractTermsInvalid,
            ContractReason.ContractTermsInvalid, ContractReason.ContractChanged, ContractReason.ContractTermsInvalid,
        }, f.Transport.ContractResultsFor(Customer).Select(r => r.Reason));
        Assert.Equal(0, f.Contracts.OpenCount);
        Assert.Equal(CustomerGold, f.Inventory.Gold(f.CustomerId));
        Assert.Equal(SwordId, f.Inventory.CurrentInventory(f.CustomerId).At(new InventoryLocation(Safe, 0)).InstanceId);
        Assert.DoesNotContain(f.Harness.Saves.Jobs, j => j.Escrow is { Holds.Count: > 0 });
    }

    [Fact]
    public void Accepting_an_enchant_contract_needs_the_item_s_job()
    {
        var f = new Fixture();
        f.ProposeEnchant(1, [f.EnchantItem(), f.EnchantStones(ContractSupplier.Customer)]);
        f.Harness.Ticks(4);
        long id = f.Transport.ContractResultsFor(Customer).Single(r => r.RequestId == 1).ContractId;

        Assert.Equal(ContractReason.ContractJobRequired, f.Accept(10, id, Other).Reason); // no Blacksmith
        Assert.Equal(ContractReason.None, f.Accept(11, id).Reason);
        Assert.Equal(ContractStatus.Accepted, f.Contracts.StatusOf(id));
    }

    [Fact]
    public void The_crafter_s_attempt_settles_the_contract_and_mails_the_same_instance_back_at_its_new_level()
    {
        var f = new Fixture();
        long id = f.EnchantAccepted(ContractSupplier.Customer);
        f.Harness.Saves.AutoComplete = false;

        f.Harness.SendContractEnchant(Crafter, 30, f.NpcId(Forge), id);
        f.Harness.SendContractEnchant(Crafter, 30, f.NpcId(Forge), id); // a double submit
        f.Harness.Ticks(2);
        Assert.Empty(f.Transport.EnchantResultsFor(Crafter)); // rule 5: answered only after the commit
        Assert.Equal(ContractStatus.InProgress, f.Contracts.StatusOf(id)); // settling

        f.Harness.Saves.AutoComplete = true;
        f.Harness.Saves.CompleteAll();
        f.Harness.Ticks(4);

        var answer = Assert.Single(f.Transport.EnchantResultsFor(Crafter));
        Assert.Equal((30u, EnchantReason.None, SwordId, (byte)0, (byte)1), (answer.RequestId, answer.Reason, answer.InstanceId,
            answer.FromLevel, answer.StonesUsed));
        Assert.Equal(ContractStatus.None, f.Contracts.StatusOf(id)); // completed: closed

        // One save of the crafter: the op, the escrow consumed, the fee, the customer's one mail, the row completed.
        var save = f.Harness.Saves.Jobs.Single(j => j.CharacterId == f.CrafterId && j.Contract is { } c
            && c.Writes.Any(w => w.Kind == ContractWriteKind.Complete));
        Assert.Contains(save.Contract!.Writes, w => w.Kind == ContractWriteKind.WorkStarted);
        var op = Assert.Single(save.Craft!.Ops);
        Assert.Equal((CraftOpKind.Enchant, id, f.CustomerId, SwordId, (int)answer.ToLevel), (op.Kind, op.ContractId, op.RecipientId,
            op.OutputInstanceId, op.EnchantTo));
        Assert.Equal(EscrowTargetKind.Consumed, Assert.Single(save.Escrow!.Releases).ToKind);
        var mail = Assert.Single(save.Mail!.Sends).Draft;
        Assert.Equal((f.CustomerId, MailKind.ContractOutput, 0L), (mail.ToCharacterId, mail.Kind, mail.Gold));
        var sword = Assert.Single(mail.Items); // the stones were consumed, never mailed
        Assert.Equal((Sword, SwordId, 1), (sword.DefId, sword.InstanceId, sword.Count));
        Assert.True(ItemProperties.TryParse(sword.PropertiesJson!, out var properties, out _));
        Assert.Equal((answer.ToLevel, 77L, 300), ((byte)properties.Enchant, properties.CrafterId, properties.Quality));
        Assert.Equal(("atk", 20), (properties.CraftedStats.Single().StatId, properties.CraftedStats.Single().Flat));

        const long fee = 5; // 5 % of 100 (FeeRates contract)
        Assert.Equal(CrafterGold + ContractGold - fee, f.Inventory.CommittedGold(f.CrafterId));
        Assert.Equal(CustomerGold - ContractGold, f.Inventory.CommittedGold(f.CustomerId));
        Assert.Equal(1, f.Units(f.CustomerId, Stone)); // one stayed, one was escrowed and consumed
        Assert.Equal(1, f.Units(f.CrafterId, Stone)); // the crafter's own (stolen) stone untouched
        var completed = Assert.IsType<ContractCompleted>(Assert.Single(f.Log.Events, e => e is ContractCompleted));
        Assert.Equal((ContractGold, fee, true, f.NetIdOf(Sword)), (completed.Gold, completed.Fee, completed.Success, completed.OutputItemNetId));
        var enchanted = Assert.IsType<Enchanted>(Assert.Single(f.Log.Events, e => e is Enchanted));
        Assert.Equal((f.CrafterId, SwordId, (int)answer.ToLevel), (enchanted.CharacterId, enchanted.InstanceId, enchanted.ToLevel));
        Assert.Equal(fee, f.Ledger.GoldDestroyedBy(InventoryReason.Fee));
        Assert.Equal(0, f.Ledger.GoldCreated);

        // A repeat of the answered request gets its first answer; the contract is gone, so nothing else happens.
        Assert.Equal(EnchantReason.None, f.EnchantContract(30, id)!.Reason);
        Assert.Single(f.Harness.Saves.Jobs, j => j.Craft is { Ops.Count: > 0 });
    }

    [Fact]
    public void With_the_crafter_s_stones_only_its_clean_stones_are_consumed()
    {
        var f = new Fixture();
        long id = f.EnchantAccepted(ContractSupplier.Crafter);

        Assert.Equal(EnchantReason.MaterialsMissing, f.EnchantContract(30, id)!.Reason); // its one stone is stolen
        Assert.Equal(ContractStatus.Accepted, f.Contracts.StatusOf(id));
        Assert.True(f.Inventory.Rules.TryGetItem(Stone, out var stone));
        Assert.Equal(1, f.Inventory.TryAdd(f.CrafterId, stone, 0, 1, "test:stone", InventoryReason.Admin, "test").Added);

        var answer = f.EnchantContract(31, id)!;
        Assert.Equal((EnchantReason.None, (byte)1), (answer.Reason, answer.StonesUsed));
        Assert.Equal(ContractStatus.None, f.Contracts.StatusOf(id));
        var bag = f.Inventory.CurrentInventory(f.CrafterId);
        Assert.Equal(1, f.Units(f.CrafterId, Stone)); // the clean one consumed, the stolen one left
        Assert.True(bag.At(new InventoryLocation(Normal, 1)).Stolen);
        Assert.Equal(2, f.Units(f.CustomerId, Stone)); // the customer's never touched
        var save = f.Harness.Saves.Jobs.Last(j => j.Craft is { Ops.Count: > 0 });
        Assert.Equal($"{Stone}:0:1", Assert.Single(Assert.Single(save.Craft!.Ops).Inputs));
        Assert.Equal(CrafterGold + ContractGold - 5, f.Inventory.CommittedGold(f.CrafterId));
    }

    [Fact]
    public void A_refusal_after_the_contract_opened_moves_nothing()
    {
        var f = new Fixture();
        long id = f.EnchantAccepted(ContractSupplier.Customer);
        string escrowId = f.Contracts.Find(id)!.EscrowId;

        Assert.Equal(EnchantReason.StationOutOfReach, f.EnchantContract(30, id, FarForge)!.Reason);
        Assert.Equal(EnchantReason.WrongStation, f.EnchantContract(31, id, Bench)!.Reason); // the sword is the Blacksmith's
        Assert.Equal(EnchantReason.ContractNotFound, f.EnchantContract(32, 999)!.Reason);
        f.Harness.SendContractEnchant(Other, 33, f.NpcId(Forge), id); // not its crafter
        f.Harness.Ticks(2);
        Assert.Equal(EnchantReason.ContractNotFound, f.Transport.EnchantResultsFor(Other)[^1].Reason);
        int attachments = f.MailSettings.AttachmentsPerMail;
        f.MailSettings.AttachmentsPerMail = 0; // the sword no longer fits the customer's mail: checked before anything moves
        Assert.Equal(EnchantReason.ContractStateInvalid, f.EnchantContract(34, id)!.Reason);
        f.MailSettings.AttachmentsPerMail = attachments;

        Assert.Equal(ContractStatus.Accepted, f.Contracts.StatusOf(id));
        Assert.False(f.Contracts.Find(id)!.WorkStarted); // still free to cancel
        Assert.True(f.Harness.World.Escrow!.IsHeld(escrowId));
        Assert.Equal((CrafterGold, CrafterGold), (f.Inventory.Gold(f.CrafterId), f.Inventory.CommittedGold(f.CrafterId)));
        Assert.Empty(f.Log.Events.OfType<ContractCompleted>());
        Assert.Empty(f.Log.Events.OfType<Enchanted>());
        Assert.DoesNotContain(f.Harness.Saves.Jobs, j => j.Craft is { Ops.Count: > 0 } || j.Mail is { Sends.Count: > 0 });

        Assert.Equal(EnchantReason.None, f.EnchantContract(35, id)!.Reason); // the same contract then goes through
    }

    [Fact]
    public void The_crafter_may_cancel_an_accepted_enchant_contract_for_free()
    {
        var f = new Fixture();
        long id = f.EnchantAccepted(ContractSupplier.Customer);

        f.Harness.SendContract(Crafter, new ContractRequest { RequestId = 50, Action = ContractAction.Cancel, ContractId = id });
        f.Harness.Ticks(4);

        var answer = f.Transport.ContractResultsFor(Crafter)[^1];
        Assert.Equal((ContractReason.None, 0L), (answer.Reason, answer.Cost));
        var save = f.Harness.Saves.Jobs.Last(j => j.CharacterId == f.CrafterId && j.Contract is { Writes.Count: > 0 });
        var back = save.Mail!.Sends.Single(s => s.Draft.Kind == MailKind.ContractReturn).Draft;
        Assert.Equal((f.CustomerId, ContractGold), (back.ToCharacterId, back.Gold));
        var sword = Assert.Single(back.Items, i => i.InstanceId != 0);
        Assert.Equal(SwordId, sword.InstanceId);
        Assert.Equal(1, back.Items.Where(i => i.DefId == Stone).Sum(i => i.Count));
        Assert.Equal(CrafterGold, f.Inventory.CommittedGold(f.CrafterId));
    }

    [Fact]
    public void The_customer_s_cancel_and_the_expiry_return_the_same_sword_at_its_old_level_with_the_stones()
    {
        var f = new Fixture();
        long cancelled = f.EnchantAccepted(ContractSupplier.Customer);
        var held = f.Escrowed(cancelled);
        f.Harness.SendContract(Customer, new ContractRequest { RequestId = 40, Action = ContractAction.Cancel, ContractId = cancelled });
        f.TickUntil(() => f.Contracts.StatusOf(cancelled) == ContractStatus.None);

        var call = Assert.Single(f.Releaser.Calls);
        Assert.Equal((held.EscrowId, f.CustomerId, (short)MailKind.ContractReturn, (short)ContractDbState.Cancelled),
            (call.EscrowId, call.To, call.Kind, call.Close!.State));
        var sword = Assert.Single(held.Items, i => i.InstanceId != 0);
        Assert.True(ItemProperties.TryParse(sword.PropertiesJson!, out var properties, out _));
        Assert.Equal((SwordId, 0), (sword.InstanceId, properties.Enchant)); // the escrow mails it back as it was
        Assert.Equal(1, held.Items.Where(i => i.DefId == Stone).Sum(i => i.Count));

        // The customer gets its sword back in the bag for the test (the mail claim is MailWorldTests'), proposes again, and it expires.
        Assert.NotNull(f.Inventory.TryReceive(f.CustomerId, [sword], 0, "test:back", InventoryReason.Admin, "test", f.CustomerId, 4, out _));
        var at = FindInstance(f, SwordId);
        f.ProposeEnchant(41, [f.EnchantItem(at.Container, (byte)at.Slot), f.EnchantStones(ContractSupplier.Customer)]);
        f.Harness.Ticks(4);
        long expired = f.Transport.ContractResultsFor(Customer).Single(r => r.RequestId == 41).ContractId;
        Assert.Equal(ContractReason.None, f.Accept(42, expired).Reason);
        string escrowId = f.Contracts.Find(expired)!.EscrowId;
        Assert.Equal(1, f.Contracts.ExpireNow(f.CustomerId, f.Harness.Clock.UtcNowMs));
        f.TickUntil(() => f.Log.Events.OfType<ContractExpired>().Any(x => x.ContractId == expired)); // after the charge read (none)

        Assert.Contains(f.Releaser.Calls, c => c.EscrowId == escrowId && c.Close!.State == (short)ContractDbState.Expired);
        Assert.Contains(f.Log.Events, e => e is ContractExpired x && x.ContractId == expired && x.Charged == 0); // never started: no charge
        Assert.Empty(f.Log.Events.OfType<ContractCompleted>());
    }

    private static InventoryLocation FindInstance(Fixture f, long instanceId)
    {
        var bags = f.Inventory.CurrentInventory(f.CustomerId);
        for (int c = 0; c < bags.ContainerCount; c++)
        {
            var bag = bags.BagAt(c);
            for (int s = 0; s < bag.SlotCount; s++)
            {
                if (bag[s].InstanceId == instanceId)
                    return new InventoryLocation((byte)bags.Rule(c).Code, s);
            }
        }
        throw new InvalidOperationException("no such instance");
    }

    private sealed class Fixture
    {
        /// <param name="msBeforeMidnight">When above 0, the world starts that long before a UTC midnight (a large clock jump later
        /// would let the sessions' ownership locks lapse).</param>
        public Fixture(int pairCap = 0, long msBeforeMidnight = 0)
        {
            var tables = TestCombat.Tables();
            Settings = tables.Single<ContractSettings>();
            MailSettings = tables.Single<MailSettings>(); // the world's mail rules read this row (a test may tighten it)
            if (pairCap > 0)
                Settings.PairCapPerDay = pairCap;
            Bus = new EventBus(1, new FakeClock(), TestPipelineSettings.BusLimits());
            Log.Subscribe(Bus);
            Ledger.Subscribe(Bus);
            var items = ItemRules.From(tables)!;
            var map = TestMaps.Map(TestMaps.Open().Grid,
            [
                TestMaps.SpawnMarker(0, 0), TestCombat.MonsterMarker(4_000, 0), EconomyFixture.Npc(Board, 200, 0, "m1.contractBoard"),
                EconomyFixture.Npc(Forge, 0, 200, "m1.forge"), EconomyFixture.Npc(FarBoard, 5_000, 5_000, "m1.contractBoard"),
                EconomyFixture.Npc(Bench, 200, 200, "m1.bench"), EconomyFixture.Npc(FarForge, 5_000, 4_000, "m1.forge"),
            ]);
            Harness = new WorldHarness(map: map, events: new EventDispatchHook(Bus), bus: Bus,
                combat: TestCombat.Rules(tables, TestCombat.Point()), items: items, economy: EconomyRules.From(tables)!,
                progression: ProgressionRules.From(tables)!, jobs: JobRules.From(tables)!, forge: ForgeRules.From(tables)!,
                enchant: EnchantRules.From(tables)!, mail: MailRules.From(tables, items), escrowReleaser: Releaser, contracts: Settings);
            if (msBeforeMidnight > 0)
            {
                long now = Harness.Clock.UtcNowMs;
                long midnight = (ContractMath.UtcDay(now) + 1) * ContractMath.MsPerDay;
                Harness.Clock.Advance(Math.Max(0, midnight - msBeforeMidnight - now));
            }
            for (int i = 1; i <= 3; i++)
            {
                var character = Harness.Characters.Add(i, 0, new CharacterState(12, 0, "", 0, 0, 0, 0, 100, 50, 100));
                Harness.Characters.Items[character.CharacterId] = i switch
                {
                    Customer => [EconomyFixture.Row(0, EconomyFixture.Ash, 10), EconomyFixture.Row(1, EconomyFixture.Horn, 5),
                        EconomyFixture.Row(2, EconomyFixture.Ash, 1, stolen: true), EconomyFixture.Row(3, Stone, 2)],
                    // The crafter's only stone is stolen: a contract never takes it (R-5.4-03; WP-M4-10 part C).
                    Crafter => [EconomyFixture.Row(0, EconomyFixture.Ash, 2), EconomyFixture.Row(1, Stone, 1, stolen: true)],
                    _ => [EconomyFixture.Row(0, EconomyFixture.Ash, 2)],
                };
                if (i == Customer)
                {
                    // Swords to enchant (WP-M4-10 part C): one in the Safe bag, one at +9 there, one equipped.
                    Harness.Characters.Instances[character.CharacterId] =
                    [
                        new ItemInstanceRow(SwordId, Safe, 0, Sword, 0, false, false, SwordProperties(0)),
                        new ItemInstanceRow(MaxedSwordId, Safe, 1, Sword, 0, false, false, SwordProperties(EnchantLimits.MaxLevel)),
                        new ItemInstanceRow(EquippedSwordId, Equipped, 0, Sword, 0, false, false, SwordProperties(0)),
                    ];
                }
                if (i == Crafter)
                {
                    Harness.Characters.Instances[character.CharacterId] =
                    [
                        new ItemInstanceRow(ItemInstanceIds.Make(2, 1), Safe, 0, "m3.moldSword", 0, false, false, ItemInstanceIds.NewProperties(2)),
                    ];
                    Harness.Characters.JobRecords[character.CharacterId] = new JobRecord(
                        [new JobProgressRow("blacksmith", JobSlot.Main, 1, 0, 0, "{\"v\":1,\"recent\":[]}")]);
                }
                Harness.Characters.StoreGold(character.CharacterId, i == Customer ? CustomerGold : CrafterGold);
                Characters.Add(character);
                Harness.Join(i);
            }
            Contracts.ReadCharge = key => Task.FromResult(Charges.TryGetValue(key, out var found) ? found : null);
            Harness.Ticks(1);
        }

        /// <summary>The charges the "database" committed with an expiry, by key (read by the world off the tick).</summary>
        public System.Collections.Concurrent.ConcurrentDictionary<string, ContractChargeFound> Charges { get; } = new();

        /// <summary>The expiry of contract <paramref name="id"/> commits a pending charge of <paramref name="amount"/>.</summary>
        public void CommittedCharge(long id, long amount)
        {
            string key = ContractRepository.ChargeKeyOf(ContractService.ExpireKey(id));
            Charges[key] = new ContractChargeFound(new ContractChargeRow(key, id, CrafterId, amount), true);
        }

        public WorldHarness Harness { get; }
        public EventBus Bus { get; }
        public ContractSettings Settings { get; }
        public MailSettings MailSettings { get; }
        public EconomyLedger Ledger { get; } = new();
        public EventLog Log { get; } = new();
        public ListingWorldTests.FakeReleaser Releaser { get; } = new();
        public List<CharacterRecord> Characters { get; } = [];
        public long CustomerId => Characters[0].CharacterId;
        public long CrafterId => Characters[1].CharacterId;
        public ContractService Contracts => Harness.World.Contracts!;
        public InventoryService Inventory => Harness.World.Inventory!;
        public FakeTransport Transport => Harness.Transport;

        public uint NpcId(string markerId) => Harness.World.Entities.OfType<Npc>().Single(n => n.MarkerId == markerId).Id;

        public ContractSlot CustomerSlot(byte slot)
        {
            var stack = Inventory.CurrentInventory(CustomerId).At(new InventoryLocation(Normal, slot));
            return new ContractSlot { Supplier = ContractSupplier.Customer, Container = Normal, Slot = slot, ItemNetId = stack.ItemNetId, Count = 1 };
        }

        /// <summary>A sword contract (mold net id 1, two free slots) with two ash of the customer's slot 0.</summary>
        public void Propose(uint request, long gold, int deadlineHours = 1, ContractSlot[]? slots = null) =>
            Harness.SendContract(Customer, new ContractRequest
            {
                RequestId = request, Action = ContractAction.Propose, Kind = ForgeKind.Mold, DefNetId = 1, Gold = gold,
                DeadlineHours = (byte)deadlineHours, Slots = slots ?? [CustomerSlot(0), CustomerSlot(0)],
            });

        public long ProposeCommitted(long gold, uint request = 100)
        {
            Propose(request, gold);
            Harness.Ticks(4);
            var answer = Transport.ContractResultsFor(Customer).Single(r => r.RequestId == request);
            Assert.Equal(ContractReason.None, answer.Reason);
            return answer.ContractId;
        }

        public ContractResult Accept(uint request, long id, int peer = Crafter, string board = Board)
        {
            Harness.SendContract(peer, new ContractRequest { RequestId = request, NpcEntityId = NpcId(board), Action = ContractAction.Accept, ContractId = id });
            Harness.Ticks(4);
            return Transport.ContractResultsFor(peer).Last(r => r.RequestId == request);
        }

        public ForgeState StartContract(uint request, long id)
        {
            Harness.SendContractForgeStart(Crafter, request, NpcId(Forge), ForgeKind.Mold, 1, id);
            Harness.Ticks(1);
            return Transport.ForgeStatesFor(Crafter)[^1];
        }

        public int NetIdOf(string itemId)
        {
            Assert.True(Inventory.Rules.TryGetItem(itemId, out var item));
            return item.NetId;
        }

        /// <summary>The item slot of an enchant contract: the customer's sword at <paramref name="container"/>:<paramref name="slot"/>.</summary>
        public ContractSlot EnchantItem(byte container = Safe, byte slot = 0, long? instanceId = null, string item = Sword) => new()
        {
            Supplier = ContractSupplier.Customer, Container = container, Slot = slot, ItemNetId = (ushort)NetIdOf(item), Count = 1,
            InstanceId = instanceId ?? SwordId,
        };

        /// <summary>The stones slot of an enchant contract (the customer's are at Normal 3).</summary>
        public ContractSlot EnchantStones(ContractSupplier supplier, int count = 1) => new()
        {
            Supplier = supplier, Container = supplier == ContractSupplier.Customer ? Normal : (byte)0,
            Slot = supplier == ContractSupplier.Customer ? (byte)3 : (byte)0, ItemNetId = (ushort)NetIdOf(Stone), Count = (ushort)count,
        };

        public void ProposeEnchant(uint request, ContractSlot[] slots, string item = Sword) =>
            Harness.SendContract(Customer, new ContractRequest
            {
                RequestId = request, Action = ContractAction.Propose, Kind = ForgeKind.Enchant, DefNetId = (ushort)NetIdOf(item),
                Gold = ContractGold, DeadlineHours = 1, Slots = slots,
            });

        /// <summary>An enchant contract of the Safe sword with the stones of <paramref name="stones"/>, proposed and accepted.</summary>
        public long EnchantAccepted(ContractSupplier stones, uint request = 200)
        {
            ProposeEnchant(request, [EnchantItem(), EnchantStones(stones)]);
            Harness.Ticks(4);
            var answer = Transport.ContractResultsFor(Customer).Single(r => r.RequestId == request);
            Assert.Equal(ContractReason.None, answer.Reason);
            Assert.Equal(ContractReason.None, Accept(request + 1, answer.ContractId).Reason);
            return answer.ContractId;
        }

        /// <summary>The crafter's attempt on contract <paramref name="id"/> at <paramref name="station"/>; its answer once ticked.</summary>
        public EnchantResult? EnchantContract(uint request, long id, string station = Forge, int ticks = 4)
        {
            Harness.SendContractEnchant(Crafter, request, NpcId(station), id);
            Harness.Ticks(ticks);
            return Transport.EnchantResultsFor(Crafter).LastOrDefault(r => r.RequestId == request);
        }

        public EscrowRecord Escrowed(long id) => Harness.World.Escrow!.Find(Contracts.Find(id)!.EscrowId)!;

        public int Units(long characterId, string itemId)
        {
            Assert.True(Inventory.Rules.TryGetItem(itemId, out var item));
            var inventory = Inventory.CurrentInventory(characterId);
            int units = 0;
            for (int c = 0; c < inventory.ContainerCount; c++)
            {
                var bag = inventory.BagAt(c);
                for (int s = 0; s < bag.SlotCount; s++)
                {
                    if (bag[s].ItemNetId == item.NetId)
                        units += bag[s].Count;
                }
            }
            return units;
        }

        /// <summary>Ticks until <paramref name="done"/> (a release to mail runs off the world thread), at most two seconds.</summary>
        public void TickUntil(Func<bool> done)
        {
            var until = DateTime.UtcNow.AddSeconds(2);
            while (!done() && DateTime.UtcNow < until)
            {
                Harness.Ticks(1);
                Thread.Sleep(5);
            }
            Assert.True(done());
        }
    }

    internal sealed class EventLog : IEventConsumer<ContractProposed>, IEventConsumer<ContractAccepted>, IEventConsumer<ContractCompleted>,
        IEventConsumer<ContractExpired>, IEventConsumer<ContractCancelled>, IEventConsumer<Crafted>, IEventConsumer<ContractCharged>,
        IEventConsumer<Enchanted>
    {
        public List<object> Events { get; } = [];

        public void Subscribe(EventBus bus)
        {
            bus.Subscribe<ContractProposed>(this);
            bus.Subscribe<ContractAccepted>(this);
            bus.Subscribe<ContractCompleted>(this);
            bus.Subscribe<ContractExpired>(this);
            bus.Subscribe<ContractCancelled>(this);
            bus.Subscribe<Crafted>(this);
            bus.Subscribe<ContractCharged>(this);
            bus.Subscribe<Enchanted>(this);
        }

        public void Handle(in EventEnvelope envelope, in Enchanted payload) => Events.Add(payload);

        public void Handle(in EventEnvelope envelope, in ContractProposed payload) => Events.Add(payload);
        public void Handle(in EventEnvelope envelope, in ContractAccepted payload) => Events.Add(payload);
        public void Handle(in EventEnvelope envelope, in ContractCompleted payload) => Events.Add(payload);
        public void Handle(in EventEnvelope envelope, in ContractExpired payload) => Events.Add(payload);
        public void Handle(in EventEnvelope envelope, in ContractCancelled payload) => Events.Add(payload);
        public void Handle(in EventEnvelope envelope, in Crafted payload) => Events.Add(payload);
        public void Handle(in EventEnvelope envelope, in ContractCharged payload) => Events.Add(payload);
    }
}
