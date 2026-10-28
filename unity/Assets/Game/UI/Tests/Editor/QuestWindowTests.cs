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
            public uint RequestInventoryMove(byte fromContainer, byte fromSlot, byte toContainer, byte toSlot) => 0;
            public uint RequestOutpostInteract(uint npcEntityId, NpcInteraction kind) => Add($"interact {npcEntityId} {kind}");
            public uint RequestSellQuote(uint npcEntityId, byte slot, ushort count) => Add("quote");
            public uint RequestSell(uint npcEntityId, byte slot, ushort count, long quotedTotal) => Add("sell");
            public uint RequestVendorBuy(uint npcEntityId, ushort itemNetId, ushort count, long quotedTotal) => Add("buy");

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
