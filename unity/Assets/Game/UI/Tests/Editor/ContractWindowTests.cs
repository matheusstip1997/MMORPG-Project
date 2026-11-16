#nullable enable
using System.Collections.Generic;
using System.Linq;
using Game.Gameplay.Entities;
using Game.Gameplay.Prediction;
using Game.Gameplay.Streaming;
using Game.UI.Config;
using Game.UI.HUD;
using Game.UI.Menus.Contracts;
using Game.UI.Menus.Forge;
using Game.UI.ViewModels.Contracts;
using Game.UI.ViewModels.Forge;
using Game.UI.ViewModels.Hud;
using Game.UI.ViewModels.Jobs;
using Game.UI.ViewModels.Quests;
using NUnit.Framework;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Actions;

namespace Game.UI.Tests
{
    /// <summary>
    /// The contract windows (spec 5.4, 9.3 C, 9.13; M4 decisions 10-12, 14; WP-M4-10 part B): the propose form runs the server's own
    /// checks and sends one slot per input; the board opens at its yes from level 20 only, and an accept waits for its answer; the C
    /// window's list and badge come only from the server's <c>ContractList</c>; the cancel cost shown is the server's rule; the forge
    /// banner works on a contract with the crafter's slots only and sends its id; the C button hides below level 20; the journal's
    /// contracts tab and the Jobs window's portfolio tab; and the committed texts, reasons, rows, key and unlock row.
    /// </summary>
    public sealed class ContractWindowTests
    {
        private const uint BoardEntity = 11;
        private const ushort BoardNetId = 8; // NpcDef m1.contractBoard
        private const ushort Ore = 401; // ItemDef m3.oreT1
        private const ushort SwordMold = 601; // ItemDef m3.moldSword
        private const int SwordMoldDef = 1; // MoldDef m3.moldSword

        private static ConfigSet Tables()
        {
            Assert.That(ClientConfig.TryLoad(out var set, out var error), Is.True, error);
            return set;
        }

        private static WorldClientFeed Feed(ConfigSet set)
        {
            var settings = PredictionSettings.From(set);
            Assert.That(ClientWorld.TryLoadGrid("m1", out var grid, out var error), Is.True, error);
            var catalog = ActionCatalog.From(set);
            return new WorldClientFeed(set, catalog, new EntityRegistry(settings), new LocalPlayerPredictor(settings, grid!, catalog),
                null, null, settings.TickMs);
        }

        private static ContractEntry Proposal(long id, ContractRole role = ContractRole.None, ContractStatus status = ContractStatus.Proposed,
            bool started = false, long gold = 20) => new ContractEntry
        {
            ContractId = id, Status = status, Role = role, Kind = ForgeKind.Mold, DefNetId = SwordMoldDef, Gold = gold, DeadlineHours = 1,
            WorkStarted = started,
            Slots = new[]
            {
                new ContractSlot { Supplier = ContractSupplier.Customer, ItemNetId = Ore, Count = 1 },
                new ContractSlot { Supplier = ContractSupplier.Crafter, ItemNetId = 0, Count = 1 },
            },
        };

        private sealed class FakeContractRequests : IContractRequests
        {
            public List<string> Sent { get; } = new List<string>();
            public ContractSlot[] LastSlots { get; private set; } = new ContractSlot[0];
            public PortfolioRequest? LastPortfolio { get; private set; }

            private uint Add(string what)
            {
                Sent.Add(what);
                return (uint)Sent.Count;
            }

            public uint RequestBoard(uint npcEntityId, byte jobNetId, ForgeKind kind, long afterContractId) =>
                Add($"board {npcEntityId} {jobNetId} {kind} {afterContractId}");

            public uint Propose(ForgeKind kind, int defNetId, long gold, int deadlineHours, IReadOnlyList<ContractSlot> slots)
            {
                LastSlots = slots.ToArray();
                return Add($"propose {kind} {defNetId} {gold} {deadlineHours}");
            }

            public uint Accept(uint npcEntityId, long contractId) => Add($"accept {npcEntityId} {contractId}");

            public uint CancelContract(long contractId) => Add($"cancel {contractId}");

            public uint RequestMyContracts() => Add("mine");

            public uint RequestPortfolio(PortfolioRequest request)
            {
                LastPortfolio = request;
                return Add($"portfolio {request.Action} {request.ContractId} {request.TargetEntityId} {request.ShowcaseSlot} {request.InstanceId}");
            }
        }

        private sealed class Rig
        {
            public Rig()
            {
                var set = Tables();
                Set = set;
                Hud = HudConfig.From(set);
                Config = ContractConfig.From(set);
                Feed = ContractWindowTests.Feed(set);
                Feed.ContractRequests = Requests;
                Window = new ContractWindowViewModel(Config);
                Binder = new ContractBinder(Config, Window, Board, Toast, 2_000, () => 0, readToggleKey: false)
                {
                    Unlocked = () => Unlocked, UnlockLevel = 20,
                };
                Binder.Bind(Feed);
            }

            public ConfigSet Set { get; }
            public HudConfig Hud { get; }
            public ContractConfig Config { get; }
            public WorldClientFeed Feed { get; }
            public FakeContractRequests Requests { get; } = new FakeContractRequests();
            public ContractWindowViewModel Window { get; }
            public ContractBoardViewModel Board { get; } = new ContractBoardViewModel();
            public ToastViewModel Toast { get; } = new ToastViewModel();
            public ContractBinder Binder { get; }
            public bool Unlocked { get; set; } = true;

            public void BoardSaysYes()
            {
                Feed.SetNpcTarget(BoardEntity, BoardNetId);
                Feed.RaiseOutpostInteracted(new OutpostInteractResult { RequestId = 1, NpcEntityId = BoardEntity, Kind = NpcInteraction.Contracts });
                Binder.Frame(0);
            }
        }

        private static ContractFormModel SwordForm(ContractConfig config)
        {
            var form = new ContractFormModel(config);
            for (int i = 0; i < form.Definitions.Count && form.Definition?.Id != "m3.moldSword"; i++)
                form.Step(1);
            Assert.That(form.Definition?.Id, Is.EqualTo("m3.moldSword"));
            return form;
        }

        // ---- the propose form

        [Test]
        public void The_form_runs_the_servers_checks_and_sends_one_slot_per_input()
        {
            var config = new Rig().Config;
            var form = SwordForm(config);
            var ore = new InventorySlotEntry { Slot = 0, ItemNetId = Ore, Count = 2 };
            var stolen = new InventorySlotEntry { Slot = 1, ItemNetId = Ore, Count = 5, Stolen = true };
            form.SetBag(new[] { ore, stolen });

            Assert.That(form.Check(), Is.EqualTo(ContractReason.ContractTermsInvalid)); // no stack chosen yet
            Assert.That(form.Candidates(0).Select(e => e.Slot), Is.EqualTo(new byte[] { 0 })); // never a stolen stack (R-5.4-03)
            Assert.That(form.Assign(0, stolen), Is.EqualTo("ContractNotTradable"));
            Assert.That(form.Assign(0, ore), Is.Null);
            Assert.That(form.Assign(1, ore), Is.Null); // two units: one per slot
            Assert.That(form.Candidates(1).Count, Is.EqualTo(1)); // the stack keeps a unit for slot 1 after slot 0's
            form.Gold = config.MinGold - 1;
            Assert.That(form.Check(), Is.EqualTo(ContractReason.ContractValueTooLow));
            form.Gold = config.MinGold;
            form.DeadlineHours = 0;
            Assert.That(form.Check(), Is.EqualTo(ContractReason.ContractDeadlineInvalid));
            form.DeadlineHours = config.MaxDeadlineHours;
            Assert.That(form.Check(), Is.EqualTo(ContractReason.None));
            var slots = form.ToSlots();
            Assert.That(slots.Select(s => (s.Supplier, s.Slot, s.ItemNetId, s.Count)), Is.EqualTo(new[]
            {
                (ContractSupplier.Customer, (byte)0, Ore, (ushort)1), (ContractSupplier.Customer, (byte)0, Ore, (ushort)1),
            }));

            form.Switch(1); // the crafter supplies the second slot: any material it takes, or one named
            Assert.That(form.ToSlots()[1].Supplier, Is.EqualTo(ContractSupplier.Crafter));
            Assert.That(form.ToSlots()[1].ItemNetId, Is.EqualTo((ushort)0));
            form.CycleCrafterItem(1);
            Assert.That(form.ToSlots()[1].ItemNetId, Is.Not.EqualTo((ushort)0));
            Assert.That(form.Check(), Is.EqualTo(ContractReason.None));
        }

        [Test]
        public void A_proposal_sends_the_form_once_and_its_answer_toasts_and_resets_the_slots()
        {
            var rig = new Rig();
            rig.Binder.Toggle();
            Assert.That(rig.Window.Open, Is.True);
            rig.Binder.SelectTab(ContractTab.Propose);
            rig.Feed.RaiseInventory(new InventoryContents { Slots = new[] { new InventorySlotEntry { Slot = 0, ItemNetId = Ore, Count = 2 } } });
            var form = rig.Window.Form;
            while (form.Definition?.Id != "m3.moldSword")
                rig.Binder.StepDefinition(1);
            rig.Binder.OpenPicker(0);
            rig.Binder.PickRow(0);
            rig.Binder.OpenPicker(1);
            rig.Binder.PickRow(0);
            rig.Binder.SetGold("40");
            rig.Binder.SetDeadline("6");
            rig.Binder.Propose();
            rig.Binder.Propose(); // one at a time
            Assert.That(rig.Requests.Sent, Is.EqualTo(new[] { $"propose Mold {SwordMoldDef} 40 6" }));
            Assert.That(rig.Requests.LastSlots.Length, Is.EqualTo(2));

            rig.Feed.RaiseContractAnswered(new ContractResult { RequestId = 1, Action = ContractAction.Propose, ContractId = 3, Gold = 40 });
            Assert.That((rig.Window.PendingAction, rig.Toast.TextId, rig.Toast.ArgValue), Is.EqualTo((0u, ContractTextIds.ToastProposed, "40")));
            Assert.That(form.Slots.All(s => s.Stack == null), Is.True); // the units are in escrow now
        }

        // ---- the board

        [Test]
        public void The_board_opens_at_its_yes_from_level_20_and_an_accept_waits_for_its_answer()
        {
            var rig = new Rig { Unlocked = false };
            rig.BoardSaysYes();
            Assert.That((rig.Board.Open, rig.Toast.TextId, rig.Toast.ArgValue), Is.EqualTo((false, ContractTextIds.Locked, "20")));
            Assert.That(rig.Requests.Sent, Is.Empty);

            rig.Unlocked = true;
            rig.BoardSaysYes();
            Assert.That(rig.Board.Open, Is.True);
            Assert.That(rig.Requests.Sent, Is.EqualTo(new[] { $"board {BoardEntity} 0 None 0" }));
            rig.Feed.RaiseContractBoard(new ContractBoard { RequestId = 9, Rows = new[] { Proposal(5) } }); // not the one asked for
            Assert.That(rig.Board.HasPage, Is.False);
            rig.Feed.RaiseContractBoard(new ContractBoard { RequestId = 1, Rows = new[] { Proposal(5), Proposal(6) }, HasMore = true });
            Assert.That(rig.Board.Rows.Count, Is.EqualTo(2));

            rig.Binder.SelectRow(1);
            rig.Binder.Accept();
            rig.Binder.Accept(); // one at a time
            Assert.That(rig.Requests.Sent.Last(), Is.EqualTo($"accept {BoardEntity} 6"));
            Assert.That(rig.Requests.Sent.Count, Is.EqualTo(2));
            rig.Feed.RaiseContractAnswered(new ContractResult { RequestId = 2, Action = ContractAction.Accept, ContractId = 6 });
            Assert.That((rig.Board.PendingAccept, rig.Toast.TextId), Is.EqualTo((0u, ContractTextIds.ToastAccepted)));
            Assert.That(rig.Requests.Sent.Last(), Is.EqualTo($"board {BoardEntity} 0 None 0")); // the board as it is now

            rig.Binder.NextPage(); // ignored: the page asked for is not back yet
            rig.Feed.RaiseContractBoard(new ContractBoard { RequestId = 3, Rows = new[] { Proposal(5), Proposal(7) }, HasMore = true });
            rig.Binder.NextPage();
            Assert.That(rig.Requests.Sent.Last(), Is.EqualTo($"board {BoardEntity} 0 None 7"));

            rig.Feed.SetNpcTarget(0, 0);
            rig.Binder.Frame(0);
            Assert.That(rig.Board.Open, Is.False); // out of reach
        }

        [Test]
        public void A_refused_accept_shows_its_reason_and_reads_the_board_again()
        {
            var rig = new Rig();
            rig.BoardSaysYes();
            rig.Feed.RaiseContractBoard(new ContractBoard { RequestId = 1, Rows = new[] { Proposal(5) } });
            rig.Binder.SelectRow(0);
            rig.Binder.Accept();
            rig.Feed.RaiseContractAnswered(new ContractResult { RequestId = 2, Action = ContractAction.Accept, Reason = ContractReason.ContractPairCap });
            Assert.That(rig.Toast.ReasonCode, Is.EqualTo("ContractPairCap"));
            Assert.That(rig.Requests.Sent.Last(), Does.StartWith("board"));
        }

        // ---- the C window

        [Test]
        public void The_list_and_the_badge_come_only_from_the_servers_contract_list()
        {
            var rig = new Rig();
            Assert.That((rig.Window.Badge, rig.Window.HasList), Is.EqualTo((0, false)));
            rig.Feed.RaiseContractList(new ContractList
            {
                Badge = 3, Rows = new[] { Proposal(1, ContractRole.Customer), Proposal(2, ContractRole.Crafter, ContractStatus.Accepted) },
            });
            Assert.That((rig.Window.Badge, rig.Window.Mine.Count), Is.EqualTo((3, 2)));
            Assert.That(rig.Feed.Contracts!.Badge, Is.EqualTo((ushort)3));

            var late = new ContractWindowViewModel(rig.Config); // a HUD bound after the list came reads it from the feed
            var binder = new ContractBinder(rig.Config, late, new ContractBoardViewModel(), new ToastViewModel(), 2_000, () => 0, readToggleKey: false);
            binder.Bind(rig.Feed);
            Assert.That(late.Badge, Is.EqualTo(3));
        }

        [Test]
        public void The_cancel_cost_shown_is_the_servers_rule()
        {
            const int percent = 10;
            Assert.That(ContractBinder.CancelCost(Proposal(1, ContractRole.Customer), percent), Is.EqualTo(0)); // an open proposal: free
            Assert.That(ContractBinder.CancelCost(Proposal(1, ContractRole.Customer, ContractStatus.Accepted, started: true), percent), Is.EqualTo(-1));
            Assert.That(ContractBinder.CancelCost(Proposal(1, ContractRole.Crafter, ContractStatus.Accepted), percent), Is.EqualTo(0));
            Assert.That(ContractBinder.CancelCost(Proposal(1, ContractRole.Crafter, ContractStatus.Accepted, started: true, gold: 100), percent),
                Is.EqualTo(10));
        }

        [Test]
        public void A_cancel_waits_for_its_answer_and_toasts_its_cost()
        {
            var rig = new Rig();
            rig.Feed.RaiseContractList(new ContractList
            {
                Rows = new[] { Proposal(2, ContractRole.Crafter, ContractStatus.Accepted, started: true, gold: 100) },
            });
            rig.Binder.CancelContract(2);
            rig.Binder.CancelContract(2);
            Assert.That(rig.Requests.Sent, Is.EqualTo(new[] { "cancel 2" }));
            rig.Feed.RaiseContractAnswered(new ContractResult { RequestId = 1, Action = ContractAction.Cancel, ContractId = 2, Cost = 10 });
            Assert.That((rig.Toast.TextId, rig.Toast.ArgValue), Is.EqualTo((ContractTextIds.ToastCancelCost, "10")));
            rig.Binder.ViewCrafter(2);
            Assert.That(rig.Requests.LastPortfolio!.ContractId, Is.EqualTo(2L));
            Assert.That(rig.Window.Portfolio.Shown, Is.True);
            rig.Feed.RaisePortfolio(new PortfolioState { RequestId = 2, Reason = ContractReason.PortfolioHidden });
            Assert.That((rig.Window.Portfolio.Shown, rig.Toast.ReasonCode), Is.EqualTo((false, "PortfolioHidden")));
        }

        // ---- the level gate

        [Test]
        public void Below_level_20_the_C_button_hides_and_its_key_opens_nothing()
        {
            var rig = new Rig { Unlocked = false };
            rig.Binder.Toggle();
            Assert.That(rig.Window.Open, Is.False);

            var view = new HudView(rig.Hud);
            Assert.That(view.Unlocked(HudElementIds.ContractWindow), Is.True); // before the first level, as every placed element
            Assert.That(view.UnlockLevel(HudElementIds.ContractWindow), Is.EqualTo(20));
            var targets = new Dictionary<string, ShortcutTarget>
            {
                ["mail"] = new ShortcutTarget(new ToastViewModel(), () => false, () => { }, "M"),
                ["contracts"] = new ShortcutTarget(rig.Window, () => false, () => { }, "N", () => rig.Window.Badge,
                    lockedBy: HudElementIds.ContractWindow),
            };
            var row = new ShortcutRowPresenter(rig.Hud, view, new ShortcutRowViewModel(new MemoryLocalSettings(), new[] { "mail", "contracts" }, true),
                targets);
            row.Bind();
            view.ApplyLevel(19);
            Assert.That(row.VisibleLabels(), Is.EqualTo(new[] { rig.Hud.Text.Get(HudTextIds.ShortcutLabel("mail")) }));
            Assert.That(view.Unlocked(HudElementIds.ContractWindow), Is.False);
            view.ApplyLevel(20);
            Assert.That(row.VisibleLabels(), Is.EqualTo(new[]
            {
                rig.Hud.Text.Get(HudTextIds.ShortcutLabel("mail")), rig.Hud.Text.Get(HudTextIds.ShortcutLabel("contracts")),
            }));
            row.Dispose();
        }

        // ---- the forge banner

        private sealed class FakeForgeRequests : IForgeRequests
        {
            public long LastContractId { get; private set; } = -1;
            public List<ForgeInput> LastInputs { get; private set; } = new List<ForgeInput>();

            public uint StartForge(uint stationEntityId, ForgeKind kind, int defNetId, IReadOnlyList<ForgeInput> inputs, long contractId = 0)
            {
                LastInputs = inputs.ToList();
                LastContractId = contractId;
                return 1;
            }

            public uint ForgeAction(int actionNetId) => 2;
            public uint FinishForge() => 3;
            public uint CancelForge() => 4;
        }

        [Test]
        public void The_forge_banner_works_on_a_contract_with_the_crafters_slots_only_and_sends_its_id()
        {
            var set = Tables();
            var forge = ForgeConfig.From(set);
            var feed = Feed(set);
            var requests = new FakeForgeRequests();
            feed.ForgeRequests = requests;
            var model = new ForgeWindowViewModel(forge.Rules);
            var binder = new ForgeBinder(forge, HudConfig.From(set).Text, model, new ToastViewModel(), 2_000, () => 0);
            binder.Bind(feed);
            var ore = new InventorySlotEntry { Slot = 3, ItemNetId = Ore, Count = 1 };
            feed.RaiseInventory(new InventoryContents
            {
                Slots = new[] { ore },
                Containers = new[] { new InventoryContainerEntry { Code = forge.MoldContainer, Slots = new[] { new InventorySlotEntry { Slot = 0, ItemNetId = SwordMold, Count = 1, InstanceId = 9 } } } },
            });
            feed.SetNpcTarget(21, 0);
            feed.RaiseOutpostInteracted(new OutpostInteractResult { RequestId = 1, NpcEntityId = 21, Kind = NpcInteraction.Forge });
            feed.RaiseContractList(new ContractList
            {
                Rows = new[]
                {
                    Proposal(7, ContractRole.Crafter, ContractStatus.Accepted), Proposal(8, ContractRole.Customer, ContractStatus.Accepted),
                },
            });
            Assert.That(model.ContractOffers.Select(o => o.ContractId), Is.EqualTo(new[] { 7L })); // the crafter's, accepted, made here

            binder.WorkOnContract();
            Assert.That((model.Contract?.ContractId, model.Mold?.Id, model.CanStart), Is.EqualTo(((long?)7, "m3.moldSword", false)));
            Assert.That(model.IsCustomerSlot(0), Is.True);
            Assert.That(model.Assign(0, forge.NormalBag, ore, forge.NormalBag), Is.EqualTo("SlotMaterialInvalid")); // from the escrow only
            Assert.That(model.Assign(1, forge.NormalBag, ore, forge.NormalBag), Is.Null);
            Assert.That(model.CanStart, Is.True);
            binder.StartForge();
            Assert.That(requests.LastContractId, Is.EqualTo(7L));
            Assert.That(requests.LastInputs.Select(i => i.Slot), Is.EqualTo(new byte[] { 3 })); // the crafter's slot only
            Assert.That(model.SessionContractId, Is.EqualTo(7L));

            var own = new ForgeWindowViewModel(forge.Rules); // without a contract the window starts its own craft as before
            own.OpenAt(21, NpcInteraction.Forge);
            Assert.That(own.Contract, Is.Null);
        }

        // ---- the journal and the Jobs window

        [Test]
        public void The_journal_lists_the_own_contracts_and_the_jobs_window_reads_the_portfolio()
        {
            var quests = new QuestBoardViewModel(5);
            quests.ToggleJournal();
            quests.SetContracts(new[] { Proposal(1, ContractRole.Customer) });
            quests.ShowJournalTab(true);
            Assert.That((quests.JournalContracts, quests.Contracts.Count), Is.EqualTo((true, 1)));
            quests.Close();
            quests.ToggleJournal();
            Assert.That(quests.JournalContracts, Is.False); // the journal opens on its runs

            var rig = new Rig();
            var jobs = new JobsWindowViewModel(JobsConfig.From(rig.Set).Rules);
            jobs.ShowPortfolio();
            Assert.That(jobs.PortfolioTab, Is.True);
            jobs.SelectTab(0);
            Assert.That(jobs.PortfolioTab, Is.False);

            var own = new OwnPortfolioViewModel();
            var portfolio = new PortfolioBinder(own, rig.Toast, 2_000, () => 0, rig.Config.Inventory.NormalBagCode);
            portfolio.Bind(rig.Feed);
            rig.Feed.RaiseInventory(new InventoryContents { Slots = new[] { new InventorySlotEntry { Slot = 4, ItemNetId = 50, Count = 1, InstanceId = 77 } } });
            portfolio.Request();
            Assert.That(rig.Requests.Sent.Last(), Is.EqualTo("portfolio View 0 0 0 0"));
            portfolio.OpenPinPicker(2);
            portfolio.PickPin(0); // the view is still out: one request at a time
            Assert.That(rig.Requests.Sent.Count, Is.EqualTo(1));
            rig.Feed.RaisePortfolio(new PortfolioState { RequestId = 1, Own = true });
            Assert.That(own.State, Is.Not.Null);
            portfolio.OpenPinPicker(2);
            portfolio.PickPin(0);
            Assert.That(rig.Requests.Sent.Last(), Is.EqualTo("portfolio Pin 0 0 2 77"));
            rig.Feed.RaisePortfolio(new PortfolioState { RequestId = 2, Reason = ContractReason.PortfolioNotCrafter });
            Assert.That(rig.Toast.ReasonCode, Is.EqualTo("PortfolioNotCrafter"));
        }

        // ---- data

        [Test]
        public void Every_text_reason_row_key_and_unlock_is_committed()
        {
            var set = Tables();
            var ui = new HashSet<string>(set.TryGet(typeof(UiStrings), out var table) ? table.Rows.Cast<UiStrings>().Select(r => r.Id) : new string[0]);
            var reasons = new HashSet<string>(set.TryGet(typeof(ReasonCodeStrings), out var codes) ? codes.Rows.Cast<ReasonCodeStrings>().Select(r => r.Code) : new string[0]);

            Assert.That(ContractTextIds.All.Where(id => !ui.Contains(id)), Is.Empty);
            Assert.That(System.Enum.GetNames(typeof(ContractReason)).Where(n => n != "None" && !reasons.Contains(n)), Is.Empty);
            Assert.That(ui.Contains(OutpostTextIds.PromptOf(NpcKind.ContractBoard)), Is.True);
            Assert.That(ui.Contains(HudTextIds.ShortcutLabel("contracts")) && ui.Contains(HudTextIds.ShortcutName("contracts")), Is.True);
            Assert.That(set.TryGet(typeof(HudLayout), out var layout), Is.True);
            var rows = layout!.Rows.Cast<HudLayout>().ToList();
            Assert.That(rows.Any(r => r.Id == HudElementIds.ContractWindow) && rows.Any(r => r.Id == HudElementIds.ContractBoard), Is.True);
            Assert.That(rows.Single(r => r.Id == HudElementIds.ShortcutRow).Buttons, Does.Contain("contracts"));
            Assert.That(Hotkeys.Find(set, Hotkeys.ToggleContracts)?.Binding, Is.EqualTo("<Keyboard>/n"));
            Assert.That(set.TryGet(typeof(UnlockSchedule), out var unlocks), Is.True);
            var unlock = unlocks!.Rows.Cast<UnlockSchedule>().Single(r => r.Id == "contracts");
            Assert.That(unlock.Level, Is.EqualTo(20));
            Assert.That(unlock.HudElements, Is.EqualTo(new[] { HudElementIds.ContractWindow, HudElementIds.ContractBoard }));

            var config = ContractConfig.From(set);
            Assert.That(config.IsBoard(BoardNetId), Is.True);
            Assert.That((config.MinGold, config.MinDeadlineHours, config.MaxDeadlineHours, config.CancelCostPercent), Is.EqualTo((10L, 1, 72, 10)));
        }
    }
}
