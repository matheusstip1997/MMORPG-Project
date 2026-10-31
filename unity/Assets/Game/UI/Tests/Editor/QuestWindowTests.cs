#nullable enable
using System.Collections.Generic;
using System.Linq;
using Game.Gameplay.Entities;
using Game.Gameplay.Prediction;
using Game.Gameplay.Streaming;
using Game.UI.Config;
using Game.UI.HUD;
using Game.UI.Menus.Outpost;
using Game.UI.Menus.Quests;
using Game.UI.ViewModels.Hud;
using Game.UI.ViewModels.Outpost;
using Game.UI.ViewModels.Quests;
using NUnit.Framework;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Actions;
using UnityEngine.UIElements;

namespace Game.UI.Tests
{
    /// <summary>
    /// The quest board, run builder and run tracker (spec 5.5, 9.3, 9.13 M2; WP-M2-6): picks in run order up to the cap, the preview
    /// with the step bonus, accept with the board's epoch, the run's hand-in, forfeit and claim buttons, the binder's requests and
    /// toasts, the outpost window's Quests button, the presenters on a real panel with the committed tables, and the committed texts.
    /// </summary>
    public sealed class QuestWindowTests
    {
        private const uint OutpostNpc = 7;
        private const ushort Demon = 1, Horn = 1, Sigil = 301;

        private static ConfigSet Tables()
        {
            Assert.That(ClientConfig.TryLoad(out var set, out var error), Is.True, error);
            return set;
        }

        private static QuestBoard Board(uint epoch = 5, int offers = 6) => new QuestBoard
        {
            NpcEntityId = OutpostNpc,
            Epoch = epoch,
            RefreshInMs = 90_000,
            Offers = Enumerable.Range(0, offers).Select(i => new QuestOffer
            {
                Slot = (byte)i, Kind = (QuestKind)(1 + i % 3), MonsterNetId = Demon, ItemNetId = (ushort)(i % 3 == 1 ? Horn : i % 3 == 2 ? Sigil : 0),
                Count = (ushort)(5 + i), RewardExp = 100 * (i + 1), RewardPercent = 100,
            }).ToArray(),
        };

        private static RunStatus Run(RunState state, params (QuestKind Kind, RunStepState State, ushort Progress)[] steps) => new RunStatus
        {
            Revision = 1,
            State = state,
            BonusPercent = 10,
            ClaimExp = 330,
            Steps = steps.Select(s => new RunStepEntry
            {
                Kind = s.Kind, MonsterNetId = Demon, ItemNetId = s.Kind == QuestKind.Kill ? (ushort)0 : Horn, Required = 3, Progress = s.Progress,
                RewardExp = 150, State = s.State,
            }).ToArray(),
        };

        // ---- view models

        [Test]
        public void Picks_keep_their_order_stop_at_the_run_cap_and_preview_the_bonus()
        {
            var set = Tables();
            var quests = QuestConfig.From(set, HudConfig.From(set), InventoryConfig.From(set));
            Assert.That(quests.MaxStepsPerRun, Is.EqualTo(5));
            var model = new QuestBoardViewModel(quests.MaxStepsPerRun);
            model.OpenFor(OutpostNpc);
            model.SetBoard(Board());

            foreach (byte slot in new byte[] { 3, 1, 0, 5, 2 })
                Assert.That(model.Toggle(slot), Is.True);
            Assert.That(model.Toggle(4), Is.False); // the run is full
            Assert.That(model.Toggle(1), Is.True); // unpicked
            Assert.That(model.Picked, Is.EqualTo(new byte[] { 3, 0, 5, 2 }));
            Assert.That(model.PickedRewards(), Is.EqualTo(400 + 100 + 600 + 300));
            Assert.That(quests.PreviewExp(model.PickedRewards(), model.Picked.Count), Is.EqualTo(1_400 * 130 / 100)); // 4 steps: +30 %

            var intent = model.Confirm()!.Value;
            Assert.That(intent.Epoch, Is.EqualTo(5u));
            Assert.That(intent.Slots, Is.EqualTo(new byte[] { 3, 0, 5, 2 }));
        }

        [Test]
        public void A_new_board_epoch_drops_the_picks_and_a_run_hides_the_builder()
        {
            var model = new QuestBoardViewModel(5);
            model.OpenFor(OutpostNpc);
            model.SetBoard(Board(epoch: 5));
            model.Toggle(2);
            model.SetBoard(Board(epoch: 5));
            Assert.That(model.Picked.Count, Is.EqualTo(1)); // the same board again
            model.SetBoard(Board(epoch: 6));
            Assert.That(model.Picked, Is.Empty);

            model.Toggle(0);
            model.SetRun(Run(RunState.Active, (QuestKind.Kill, RunStepState.Active, 1)));
            Assert.That((model.HasRun, model.Picked.Count, model.Confirm().HasValue), Is.EqualTo((true, 0, false)));
            Assert.That(model.Toggle(1), Is.False);
            model.SetRun(new RunStatus { State = RunState.None });
            Assert.That(model.HasRun, Is.False);
        }

        [Test]
        public void The_tracker_shows_an_active_or_completed_run_only()
        {
            var tracker = new RunTrackerViewModel();
            Assert.That(tracker.Visible, Is.False);
            tracker.Set(Run(RunState.Active, (QuestKind.Kill, RunStepState.Done, 3), (QuestKind.Deliver, RunStepState.Active, 1)));
            Assert.That((tracker.Visible, tracker.DoneSteps), Is.EqualTo((true, 1)));
            tracker.Set(new RunStatus { State = RunState.None });
            Assert.That(tracker.Visible, Is.False);
        }

        // ---- binder

        private sealed class FakeRequests : IQuestRequests
        {
            public List<string> Sent { get; } = new List<string>();

            public uint RequestQuestRun(uint npcEntityId, QuestRunAction action, uint epoch, byte stepIndex, byte[] slots)
            {
                Sent.Add($"{action} {npcEntityId} {epoch} {stepIndex} [{string.Join(",", slots)}]");
                return (uint)Sent.Count;
            }
        }

        private sealed class FakeWorldRequests : IWorldRequests
        {
            public List<string> Sent { get; } = new List<string>();
            public uint RequestInventoryMove(byte fromContainer, byte fromSlot, byte toContainer, byte toSlot, ushort count = 0) => 0;
            public uint RequestOutpostInteract(uint npcEntityId, NpcInteraction kind) => Add($"interact {npcEntityId} {kind}");
            public uint RequestSellQuote(uint npcEntityId, byte slot, ushort count) => Add("quote");
            public uint RequestSell(uint npcEntityId, byte slot, ushort count, long quotedTotal) => Add("sell");
            public uint RequestVendorBuy(uint npcEntityId, ushort itemNetId, ushort count, long quotedTotal) => Add("buy");
            public uint RequestSkillTree(SkillTreeOp op, string target, byte slot) => 0;

            private uint Add(string request)
            {
                Sent.Add(request);
                return (uint)Sent.Count;
            }
        }

        private static WorldClientFeed Feed(ConfigSet set)
        {
            var settings = PredictionSettings.From(set);
            Assert.That(ClientWorld.TryLoadGrid("m1", out var grid, out var error), Is.True, error);
            var catalog = ActionCatalog.From(set);
            return new WorldClientFeed(set, catalog, new EntityRegistry(settings), new LocalPlayerPredictor(settings, grid!, catalog),
                null, null, settings.TickMs);
        }

        private sealed class Rig
        {
            public Rig()
            {
                var set = Tables();
                Feed = Feed(set);
                Feed.QuestRequests = Requests;
                Feed.Requests = WorldRequests;
                Board = new QuestBoardViewModel(5);
                Binder = new QuestBinder(Board, Tracker, Toast, 2_000, () => 0);
                Binder.Bind(Feed);
                Outpost = new OutpostBinder(OutpostConfig.From(set, InventoryConfig.From(set)), Sell, new VendorWindowViewModel(),
                    new NpcPromptViewModel(), new GoldViewModel(), Toast, 2_000, () => 0, readInteractKey: false);
                Outpost.Bind(Feed);
            }

            public WorldClientFeed Feed { get; }
            public FakeRequests Requests { get; } = new FakeRequests();
            public FakeWorldRequests WorldRequests { get; } = new FakeWorldRequests();
            public QuestBoardViewModel Board { get; }
            public RunTrackerViewModel Tracker { get; } = new RunTrackerViewModel();
            public ToastViewModel Toast { get; } = new ToastViewModel();
            public OutpostWindowViewModel Sell { get; } = new OutpostWindowViewModel();
            public QuestBinder Binder { get; }
            public OutpostBinder Outpost { get; }

            /// <summary>The player at the outpost, its sale window open, then its Quests button and the server's yes and board.</summary>
            public void OpenBoard(QuestBoard board)
            {
                Feed.SetNpcTarget(OutpostNpc, 1);
                Outpost.Interact();
                Feed.RaiseOutpostInteracted(new OutpostInteractResult { RequestId = 1, NpcEntityId = OutpostNpc, Kind = NpcInteraction.Sell });
                Outpost.OpenQuests();
                Feed.RaiseOutpostInteracted(new OutpostInteractResult { RequestId = 2, NpcEntityId = OutpostNpc, Kind = NpcInteraction.Quests });
                Feed.RaiseQuestBoard(board);
            }
        }

        [Test]
        public void The_outpost_windows_Quests_button_opens_the_board_in_its_place()
        {
            var rig = new Rig();

            rig.OpenBoard(Board());

            Assert.That(rig.WorldRequests.Sent, Is.EqualTo(new[] { "interact 7 Sell", "interact 7 Quests" }));
            Assert.That((rig.Sell.Open, rig.Board.Open, rig.Board.Board?.Epoch), Is.EqualTo((false, true, (uint?)5)));
        }

        [Test]
        public void A_two_step_run_is_accepted_with_the_board_epoch_then_handed_in_forfeited_and_claimed()
        {
            var rig = new Rig();
            rig.OpenBoard(Board());
            rig.Binder.ToggleOffer(1);
            rig.Binder.ToggleOffer(4);

            rig.Binder.AcceptRun();
            rig.Binder.AcceptRun(); // one accept at a time
            rig.Feed.RaiseQuestRunAnswered(new QuestRunResult { RequestId = 1, Action = QuestRunAction.Accept });
            rig.Feed.RaiseRun(Run(RunState.Active, (QuestKind.Deliver, RunStepState.Active, 0), (QuestKind.Kill, RunStepState.Active, 2)));
            rig.Binder.HandIn(0);
            rig.Feed.RaiseQuestRunAnswered(new QuestRunResult { RequestId = 2, Action = QuestRunAction.Deliver, Reason = OutpostReason.NothingToDeliver });
            Assert.That(rig.Toast.ReasonCode, Is.EqualTo("NothingToDeliver"));
            rig.Binder.Forfeit(0);
            rig.Feed.RaiseRun(Run(RunState.Completed, (QuestKind.Deliver, RunStepState.Forfeited, 0), (QuestKind.Kill, RunStepState.Done, 3)));
            rig.Binder.Claim();
            rig.Feed.RaiseQuestRunAnswered(new QuestRunResult { RequestId = 4, Action = QuestRunAction.Claim, Exp = 330 });

            Assert.That(rig.Requests.Sent, Is.EqualTo(new[] { "Accept 7 5 0 [1,4]", "Deliver 7 0 0 []", "Forfeit 7 0 0 []", "Claim 7 0 0 []" }));
            Assert.That((rig.Toast.TextId, rig.Toast.ArgValue), Is.EqualTo((QuestTextIds.ToastClaimed, "330")));
            Assert.That((rig.Tracker.Visible, rig.Tracker.DoneSteps), Is.EqualTo((true, 2)));
        }

        [Test]
        public void Leaving_the_reach_closes_the_board_and_a_disconnect_clears_the_run()
        {
            var rig = new Rig();
            rig.OpenBoard(Board());
            rig.Feed.RaiseRun(Run(RunState.Active, (QuestKind.Kill, RunStepState.Active, 0)));

            rig.Feed.SetNpcTarget(0, 0);
            rig.Binder.Frame(0);
            Assert.That(rig.Board.Open, Is.False);
            Assert.That(rig.Tracker.Visible, Is.True); // the tracker stays on the HUD away from the outpost

            rig.Feed.RaiseDisconnected("Disconnected");
            Assert.That((rig.Tracker.Visible, rig.Board.Board), Is.EqualTo((false, (QuestBoard?)null)));
        }

        // ---- the journal (WP-M2-11, user decision 2026-10-28)

        [Test]
        public void The_journal_opens_away_from_an_outpost_shows_the_run_sends_nothing_and_gives_way_to_the_board()
        {
            var rig = new Rig();
            rig.Feed.RaiseRun(Run(RunState.Completed, (QuestKind.Kill, RunStepState.Done, 3)));

            rig.Board.ToggleJournal();
            rig.Binder.Frame(0); // no NPC in reach: the journal stays open
            rig.Binder.Forfeit(0);
            rig.Binder.Claim();
            Assert.That((rig.Board.Open, rig.Board.Journal), Is.EqualTo((true, true)));
            Assert.That(rig.Requests.Sent, Is.Empty, "every run action needs the outpost");

            rig.OpenBoard(Board()); // at the outpost, the board replaces the journal
            Assert.That((rig.Board.Open, rig.Board.Journal, rig.Board.NpcEntityId), Is.EqualTo((true, false, OutpostNpc)));
            rig.Binder.Claim();
            Assert.That(rig.Requests.Sent, Is.EqualTo(new[] { "Claim 7 0 0 []" }));

            rig.Board.ToggleJournal(); // the JR button closes whatever the window shows
            Assert.That((rig.Board.Open, rig.Board.Journal), Is.EqualTo((false, false)));
        }

        [Test]
        public void The_journal_draws_the_run_without_actions_and_its_note_on_a_real_panel()
        {
            var set = Tables();
            var config = HudConfig.From(set);
            var quests = QuestConfig.From(set, config, InventoryConfig.From(set));
            var model = new QuestBoardViewModel(quests.MaxStepsPerRun);
            var board = new QuestBoardPresenter(config, new HudView(config), quests, model,
                new QuestBinder(model, new RunTrackerViewModel(), new ToastViewModel(), 2_000, () => 0));
            board.Bind();
            Assert.That(quests.JournalBinding, Is.EqualTo("<Keyboard>/j"));

            model.ToggleJournal();
            Assert.That((board.IsShown, board.BoardShown, board.RunShown), Is.EqualTo((true, false, false)));
            Assert.That((board.TitleText, board.JournalText), Is.EqualTo(("Journal", "No active run. Outposts offer quests.")));

            model.SetRun(Run(RunState.Completed, (QuestKind.Kill, RunStepState.Done, 3), (QuestKind.Deliver, RunStepState.Active, 1)));
            Assert.That((board.RunShown, board.StepCount), Is.EqualTo((true, 2)));
            Assert.That(board.JournalText, Is.EqualTo("Hand in, forfeit and claim at an outpost."));
            Assert.That(board.Step(1)[1].style.display.value, Is.EqualTo(DisplayStyle.None), "no hand-in in the journal");
            Assert.That(board.Step(1)[2].style.display.value, Is.EqualTo(DisplayStyle.None), "no forfeit in the journal");
            Assert.That(board.ClaimButton.style.display.value, Is.EqualTo(DisplayStyle.None), "no claim in the journal");

            model.OpenFor(OutpostNpc);
            Assert.That((board.TitleText, board.JournalText), Is.EqualTo((config.Text.Get(QuestTextIds.Title), "")));
            Assert.That(board.Step(1)[2].style.display.value, Is.EqualTo(DisplayStyle.Flex), "at the outpost the steps act again");
            board.Dispose();
        }

        // ---- presenters on a real panel

        [Test]
        public void The_board_and_tracker_draw_the_committed_texts_on_a_real_panel()
        {
            var set = Tables();
            var config = HudConfig.From(set);
            var quests = QuestConfig.From(set, config, InventoryConfig.From(set));
            var view = new HudView(config);
            var model = new QuestBoardViewModel(quests.MaxStepsPerRun);
            var tracker = new RunTrackerViewModel();
            var actions = new QuestBinder(model, tracker, new ToastViewModel(), 2_000, () => 0);
            var board = new QuestBoardPresenter(config, view, quests, model, actions);
            var trackerPresenter = new RunTrackerPresenter(config, view, quests, tracker);
            board.Bind();
            trackerPresenter.Bind();
            Assert.That((board.IsShown, trackerPresenter.IsShown), Is.EqualTo((false, false)));

            model.OpenFor(OutpostNpc);
            model.SetBoard(Board());
            Assert.That((board.IsShown, board.BoardShown, board.RunShown, board.OfferCount), Is.EqualTo((true, true, false, 6)));
            Assert.That(board.Offer(0).text, Is.EqualTo("Kill 5 Demon: 100 exp"));
            Assert.That(board.Offer(1).text, Is.EqualTo("Deliver 6 Demon Horn (Demon): 200 exp"));
            Assert.That(board.Offer(2).text, Is.EqualTo("Return 7 Demon Sigil (Demon): 300 exp"));
            Assert.That(board.AcceptEnabled, Is.False);
            model.Toggle(1);
            model.Toggle(0);
            Assert.That(board.Offer(1).text, Is.EqualTo("1. Deliver 6 Demon Horn (Demon): 200 exp"));
            Assert.That(board.Offer(0).text, Is.EqualTo("2. Kill 5 Demon: 100 exp"));
            Assert.That(board.PickedText, Is.EqualTo("Run: 2 of 5 quests, 330 exp with the bonus")); // (200 + 100) x 1.10
            Assert.That(board.AcceptEnabled, Is.True);

            var run = Run(RunState.Completed, (QuestKind.Kill, RunStepState.Done, 3), (QuestKind.Deliver, RunStepState.Forfeited, 0));
            model.SetRun(run);
            tracker.Set(run);
            Assert.That((board.BoardShown, board.RunShown, board.StepCount), Is.EqualTo((false, true, 2)));
            Assert.That(((Label)board.Step(0)[0]).text, Is.EqualTo("Kill 3 Demon: done"));
            Assert.That(((Label)board.Step(1)[0]).text, Is.EqualTo("Deliver 3 Demon Horn (Demon): forfeited"));
            Assert.That((board.ClaimButton.text, board.ClaimButton.style.display.value), Is.EqualTo(("Claim 330 exp", DisplayStyle.Flex)));
            Assert.That(trackerPresenter.IsShown, Is.True);
            Assert.That((trackerPresenter.TitleText, trackerPresenter.BonusText), Is.EqualTo(("Run: 2 of 2 done", "Bonus +10%  Penalty -0%")));
            Assert.That((trackerPresenter.ClaimShown, trackerPresenter.ClaimText), Is.EqualTo((true, "Claim 330 exp at an outpost")));

            board.Dispose();
            trackerPresenter.Dispose();
        }

        private static InventorySlotEntry Stack(byte slot, ushort itemNetId, ushort count) =>
            new InventorySlotEntry { Slot = slot, ItemNetId = itemNetId, Count = count };

        /// <summary>
        /// User decision 2026-10-30 (spec 9.3 <c>run_tracker</c>): an open deliver or return step shows how many of its item the
        /// player holds, from the committed inventory, counted in the container that receives the item (the one a hand-in takes
        /// from); the delivered progress stays the server's.
        /// </summary>
        [Test]
        public void The_tracker_shows_the_held_count_of_an_open_deliver_or_return_step()
        {
            var rig = new Rig();
            var set = Tables();
            var config = HudConfig.From(set);
            var tracker = new RunTrackerPresenter(config, new HudView(config), QuestConfig.From(set, config, InventoryConfig.From(set)), rig.Tracker);
            tracker.Bind();
            RunStepEntry Step(QuestKind kind, ushort item, ushort required, ushort progress, RunStepState state = RunStepState.Active) =>
                new RunStepEntry { Kind = kind, MonsterNetId = Demon, ItemNetId = item, Required = required, Progress = progress, State = state };
            RunStatus Status(ushort delivered, RunStepState deliverState) => new RunStatus
            {
                Revision = 1, State = RunState.Active,
                Steps = new[] { Step(QuestKind.Deliver, Horn, 6, delivered, deliverState), Step(QuestKind.ReturnItem, Sigil, 2, 0), Step(QuestKind.Kill, 0, 11, 5) },
            };

            rig.Feed.RaiseRun(Status(0, RunStepState.Active));
            Assert.That(tracker.Steps[0].text, Is.EqualTo("Deliver 6 Demon Horn (Demon) 0/6"), "no held count before the inventory arrived");

            // 3 horns in the Normal bag count; 2 moved to the Safe valuables bag do not (a hand-in never takes them); the sigil is in the Quest bag.
            rig.Feed.RaiseInventory(new InventoryContents
            {
                Revision = 1, SlotCount = 30, Slots = new[] { Stack(0, Horn, 2), Stack(4, Horn, 1) },
                Containers = new[]
                {
                    new InventoryContainerEntry { Code = 3, SlotCount = 10, Slots = new[] { Stack(0, Horn, 2) } },
                    new InventoryContainerEntry { Code = 4, SlotCount = 20, Slots = new[] { Stack(0, Sigil, 1) } },
                },
            });
            Assert.That(tracker.Steps[0].text, Is.EqualTo("Deliver 6 Demon Horn (Demon): 3 held, 0/6"));
            Assert.That(tracker.Steps[1].text, Is.EqualTo("Return 2 Demon Sigil (Demon): 1 held, 0/2"));
            Assert.That(tracker.Steps[2].text, Is.EqualTo("Kill 11 Demon 5/11"), "a kill step has no item");

            // The hand-in: the server's run says 3/6 and the next committed inventory has no horns left in the Normal bag.
            rig.Feed.RaiseRun(Status(3, RunStepState.Active));
            rig.Feed.RaiseInventory(new InventoryContents { Revision = 2, SlotCount = 30 });
            Assert.That(tracker.Steps[0].text, Is.EqualTo("Deliver 6 Demon Horn (Demon): 0 held, 3/6"));

            rig.Feed.RaiseRun(Status(6, RunStepState.Done));
            Assert.That(tracker.Steps[0].text, Is.EqualTo("Deliver 6 Demon Horn (Demon): done"));
            tracker.Dispose();
        }

        // ---- committed texts

        [Test]
        public void Committed_ui_strings_cover_every_quest_text_id()
        {
            var set = Tables();
            var ui = new HashSet<string>(set.TryGet(typeof(UiStrings), out var table) ? table.Rows.Cast<UiStrings>().Select(r => r.Id) : new string[0]);

            Assert.That(QuestTextIds.All.Where(id => !ui.Contains(id)), Is.Empty);
        }
    }
}
