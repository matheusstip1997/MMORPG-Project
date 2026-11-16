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
    private const byte Normal = 1, Safe = 2;
    private const long CustomerGold = 1_000, CrafterGold = 100, ContractGold = 100;
    private const int Customer = 1, Crafter = 2, Other = 3;

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
    public void The_customer_cancels_for_free_before_work_started_by_mail_and_not_after()
    {
        var f = new Fixture();
        long id = f.ProposeCommitted(ContractGold);
        f.Harness.SendContract(Customer, new ContractRequest { RequestId = 40, Action = ContractAction.Cancel, ContractId = id });
        f.TickUntil(() => f.Contracts.StatusOf(id) == ContractStatus.None);

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
        Assert.Equal(1, f.Contracts.ExpireNow(f.CrafterId, f.Harness.Clock.UtcNowMs)); // the deadline comes now (the dev console's hook)

        f.TickUntil(() => f.Contracts.StatusOf(id) == ContractStatus.None);

        Assert.True(f.Transport.ForgeStatesFor(Crafter).Any(s => s.Reason == ForgeReason.ContractExpired && s.Phase == ForgePhase.Cancelled),
            string.Join(",", f.Transport.ForgeStatesFor(Crafter).Select(s => $"{s.RequestId}:{s.Reason}:{s.Phase}")));
        Assert.Null(f.Harness.World.Forge!.Session(f.CrafterId));
        Assert.Contains(f.Log.Events, e => e is ContractExpired x && x.ContractId == id && x.CrafterId == f.CrafterId);
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

    private sealed class Fixture
    {
        public Fixture(int pairCap = 0)
        {
            var tables = TestCombat.Tables();
            Settings = tables.Single<ContractSettings>();
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
            ]);
            Harness = new WorldHarness(map: map, events: new EventDispatchHook(Bus), bus: Bus,
                combat: TestCombat.Rules(tables, TestCombat.Point()), items: items, economy: EconomyRules.From(tables)!,
                progression: ProgressionRules.From(tables)!, jobs: JobRules.From(tables)!, forge: ForgeRules.From(tables)!,
                mail: MailRules.From(tables, items), escrowReleaser: Releaser, contracts: Settings);
            for (int i = 1; i <= 3; i++)
            {
                var character = Harness.Characters.Add(i, 0, new CharacterState(12, 0, "", 0, 0, 0, 0, 100, 50, 100));
                Harness.Characters.Items[character.CharacterId] = i switch
                {
                    Customer => [EconomyFixture.Row(0, EconomyFixture.Ash, 10), EconomyFixture.Row(1, EconomyFixture.Horn, 5),
                        EconomyFixture.Row(2, EconomyFixture.Ash, 1, stolen: true)],
                    _ => [EconomyFixture.Row(0, EconomyFixture.Ash, 2)],
                };
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
            Harness.Ticks(1);
        }

        public WorldHarness Harness { get; }
        public EventBus Bus { get; }
        public ContractSettings Settings { get; }
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
        IEventConsumer<ContractExpired>, IEventConsumer<ContractCancelled>, IEventConsumer<Crafted>
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
        }

        public void Handle(in EventEnvelope envelope, in ContractProposed payload) => Events.Add(payload);
        public void Handle(in EventEnvelope envelope, in ContractAccepted payload) => Events.Add(payload);
        public void Handle(in EventEnvelope envelope, in ContractCompleted payload) => Events.Add(payload);
        public void Handle(in EventEnvelope envelope, in ContractExpired payload) => Events.Add(payload);
        public void Handle(in EventEnvelope envelope, in ContractCancelled payload) => Events.Add(payload);
        public void Handle(in EventEnvelope envelope, in Crafted payload) => Events.Add(payload);
    }
}
