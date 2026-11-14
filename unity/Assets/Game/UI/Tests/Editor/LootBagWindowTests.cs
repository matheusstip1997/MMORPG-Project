#nullable enable
using System.Collections.Generic;
using Game.Gameplay.Entities;
using Game.Gameplay.Prediction;
using Game.Gameplay.Streaming;
using Game.UI.Config;
using Game.UI.HUD;
using Game.UI.Menus.LootBag;
using Game.UI.ViewModels.Hud;
using Game.UI.ViewModels.LootBag;
using NUnit.Framework;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Actions;

namespace Game.UI.Tests
{
    /// <summary>
    /// The death loot bag window (spec 4.3 R-4.3-03, 4.4 R-4.4-01, 9.13; WP-M4-9): the view model shows the server's contents, a
    /// closed bag closes it, one take at a time, the countdowns; the binder's take requests and its toasts (back, by mail,
    /// collected for the owner, stolen, a refusal); the presenter's title by access (the owner never named), notes, rows and
    /// disabled buttons; the committed texts and the pickup prompt at a bag.
    /// </summary>
    public sealed class LootBagWindowTests
    {
        private const ushort Horn = 1; // ItemDef m1.demonHorn
        private const uint Bag = 77;

        private static ConfigSet Tables()
        {
            Assert.That(ClientConfig.TryLoad(out var set, out var error), Is.True, error);
            return set;
        }

        private static LootBagContent Content(LootBagAccess access, uint windowMs = 0, uint expiresMs = 600_000, params LootBagRowEntry[] rows) =>
            new LootBagContent { BagEntityId = Bag, Access = access, OwnerWindowLeftMs = windowMs, ExpiresInMs = expiresMs, Rows = rows };

        private static LootBagRowEntry Row(byte index, ushort count, bool stolen = false) =>
            new LootBagRowEntry { Index = index, ItemNetId = Horn, Count = count, Stolen = stolen };

        private sealed class FakeRequests : ILootBagRequests
        {
            public List<string> Sent { get; } = new List<string>();
            public uint OpenLootBag(uint bagEntityId) => Add("open " + bagEntityId);
            public uint TakeFromLootBag(uint bagEntityId, byte row) => Add($"take {bagEntityId} row {row}");
            public uint TakeAllFromLootBag(uint bagEntityId) => Add("take all " + bagEntityId);
            public uint CloseLootBag(uint bagEntityId) => Add("close " + bagEntityId);

            private uint Add(string what)
            {
                Sent.Add(what);
                return (uint)Sent.Count;
            }
        }

        private sealed class Rig
        {
            public Rig()
            {
                var set = Tables();
                Hud = HudConfig.From(set);
                var settings = PredictionSettings.From(set);
                Assert.That(ClientWorld.TryLoadGrid("m1", out var grid, out var error), Is.True, error);
                var catalog = ActionCatalog.From(set);
                Feed = new WorldClientFeed(set, catalog, new EntityRegistry(settings), new LocalPlayerPredictor(settings, grid!, catalog),
                    null, null, settings.TickMs) { LootBagRequests = Requests };
                Binder = new LootBagWindowBinder(Model, Toast, 2_000, () => Now);
                Binder.Bind(Feed);
                Presenter = new LootBagWindowPresenter(Hud, new HudView(Hud), LootBagConfig.From(set), Model, Binder, () => Now);
                Presenter.Bind();
            }

            public HudConfig Hud { get; }
            public WorldClientFeed Feed { get; }
            public FakeRequests Requests { get; } = new FakeRequests();
            public LootBagWindowViewModel Model { get; } = new LootBagWindowViewModel();
            public ToastViewModel Toast { get; } = new ToastViewModel();
            public LootBagWindowBinder Binder { get; }
            public LootBagWindowPresenter Presenter { get; }
            public double Now { get; set; }
        }

        // ---- view model

        [Test]
        public void Contents_open_the_window_and_a_closed_bag_closes_it()
        {
            var model = new LootBagWindowViewModel();
            model.SetContent(Content(LootBagAccess.Own, rows: Row(0, 3)), 0);
            Assert.That((model.Open, model.CanTake), Is.EqualTo((true, true)));

            model.SetContent(new LootBagContent { BagEntityId = 999, Closed = true }, 10); // another bag: nothing changes
            Assert.That(model.Open, Is.True);
            model.SetContent(new LootBagContent { BagEntityId = Bag, Closed = true }, 20);
            Assert.That((model.Open, model.Content), Is.EqualTo((false, (LootBagContent?)null)));
        }

        [Test]
        public void One_take_at_a_time_nothing_while_locked_and_the_countdowns_run_from_the_contents()
        {
            var model = new LootBagWindowViewModel();
            model.SetContent(Content(LootBagAccess.Locked, windowMs: 30_000, rows: Row(0, 3)), 1_000);
            Assert.That(model.CanTake, Is.False);
            Assert.That(model.WindowLeftMs(11_000), Is.EqualTo(20_000));
            Assert.That(model.WindowLeftMs(40_000), Is.EqualTo(0));
            Assert.That(model.ExpiresLeftMs(101_000), Is.EqualTo(500_000));

            model.SetContent(Content(LootBagAccess.TakeStolen, rows: Row(0, 3)), 2_000);
            model.SetPendingTake(5);
            Assert.That(model.CanTake, Is.False);
            Assert.That(model.OnResult(new LootBagResult { RequestId = 4, Op = LootBagOp.TakeAll }, out bool applied), Is.Null);
            Assert.That(applied, Is.False); // not ours
            Assert.That(model.OnResult(new LootBagResult { RequestId = 5, Op = LootBagOp.TakeAll, Taken = 3, Stolen = true }, out applied), Is.Null);
            Assert.That((applied, model.PendingTake), Is.EqualTo((true, 0u)));
            Assert.That(model.OnResult(new LootBagResult { RequestId = 6, Op = LootBagOp.TakeOne, Reason = LootBagReason.BagLocked }, out _),
                Is.EqualTo("BagLocked"));
        }

        // ---- binder

        [Test]
        public void A_row_and_take_all_send_one_take_and_close_tells_the_server()
        {
            var rig = new Rig();
            rig.Feed.RaiseLootBagContent(Content(LootBagAccess.Own, rows: new[] { Row(0, 3), Row(4, 2) }));
            rig.Binder.TakeRow(4);
            rig.Binder.TakeAll(); // one take at a time
            rig.Feed.RaiseLootBagAnswered(new LootBagResult { RequestId = 1, BagEntityId = Bag, Op = LootBagOp.TakeOne, Taken = 2 });
            rig.Binder.TakeAll();
            rig.Binder.CloseBag();

            Assert.That(rig.Requests.Sent, Is.EqualTo(new[] { "take 77 row 4", "take all 77", "close 77" }));
            Assert.That(rig.Model.Open, Is.False);
        }

        [Test]
        public void The_toasts_say_what_a_take_did_and_a_refusal_shows_its_reason()
        {
            var rig = new Rig();
            rig.Feed.RaiseLootBagContent(Content(LootBagAccess.Own, rows: Row(0, 9)));
            rig.Binder.TakeAll();
            rig.Feed.RaiseLootBagAnswered(new LootBagResult { RequestId = 1, Op = LootBagOp.TakeAll, Taken = 5, Mailed = 4 });
            Assert.That((rig.Toast.TextId, rig.Toast.ArgValue), Is.EqualTo((LootBagTextIds.ToastTakenMailed, "5")));

            rig.Feed.RaiseLootBagContent(Content(LootBagAccess.CollectForOwner, windowMs: 40_000, rows: Row(0, 9)));
            rig.Binder.TakeAll();
            rig.Feed.RaiseLootBagAnswered(new LootBagResult { RequestId = 2, Op = LootBagOp.TakeAll, Mailed = 9 });
            Assert.That(rig.Toast.TextId, Is.EqualTo(LootBagTextIds.ToastCollected));

            rig.Feed.RaiseLootBagContent(Content(LootBagAccess.TakeStolen, rows: Row(0, 9)));
            rig.Binder.TakeAll();
            rig.Feed.RaiseLootBagAnswered(new LootBagResult { RequestId = 3, Op = LootBagOp.TakeAll, Taken = 9, Stolen = true });
            Assert.That(rig.Toast.TextId, Is.EqualTo(LootBagTextIds.ToastStolen));

            rig.Feed.RaiseLootBagAnswered(new LootBagResult { RequestId = 9, Op = LootBagOp.Open, Reason = LootBagReason.OutOfRange });
            Assert.That(rig.Toast.ReasonCode, Is.EqualTo("OutOfRange"));
        }

        [Test]
        public void A_take_that_commits_after_the_window_closed_still_toasts_what_it_did()
        {
            // Review of #248: Close (or the server's Closed contents) keeps the waiting take; only a disconnect drops it.
            var rig = new Rig();
            rig.Feed.RaiseLootBagContent(Content(LootBagAccess.CollectForOwner, windowMs: 40_000, rows: Row(0, 9)));
            rig.Binder.TakeAll();
            rig.Binder.CloseBag();
            Assert.That((rig.Model.Open, rig.Model.PendingTake), Is.EqualTo((false, 1u)));
            rig.Feed.RaiseLootBagAnswered(new LootBagResult { RequestId = 1, Op = LootBagOp.TakeAll, Mailed = 9 });
            Assert.That(rig.Toast.TextId, Is.EqualTo(LootBagTextIds.ToastCollected), "the access it was asked under, not the closed window's");

            rig.Feed.RaiseLootBagContent(Content(LootBagAccess.TakeStolen, rows: Row(0, 9)));
            rig.Binder.TakeAll();
            rig.Feed.RaiseLootBagContent(new LootBagContent { BagEntityId = Bag, Closed = true }); // emptied by the take
            rig.Feed.RaiseLootBagAnswered(new LootBagResult { RequestId = 3, Op = LootBagOp.TakeAll, Taken = 9, Stolen = true });
            Assert.That(rig.Toast.TextId, Is.EqualTo(LootBagTextIds.ToastStolen));

            rig.Feed.RaiseLootBagContent(Content(LootBagAccess.Own, rows: Row(0, 9)));
            rig.Binder.TakeAll();
            rig.Feed.RaiseDisconnected("ConnectionLost");
            Assert.That((rig.Model.Open, rig.Model.PendingTake), Is.EqualTo((false, 0u)));
        }

        [Test]
        public void The_window_follows_new_contents_when_the_access_changes_while_it_is_open()
        {
            // Review of #248: the server sends new contents when the owner window ends or the party changes.
            var rig = new Rig();
            rig.Feed.RaiseLootBagContent(Content(LootBagAccess.CollectForOwner, windowMs: 2_000, rows: Row(0, 3)));
            Assert.That(rig.Presenter.TitleText, Is.EqualTo("A party member's bag"));
            Assert.That(rig.Presenter.NoteText, Is.EqualTo("What you take goes to its owner by mail."));

            rig.Feed.RaiseLootBagContent(Content(LootBagAccess.TakeStolen, rows: Row(0, 3))); // the owner window ended
            Assert.That(rig.Presenter.TitleText, Is.EqualTo("Someone's bag"));
            Assert.That(rig.Presenter.NoteText, Is.EqualTo("Taking from it marks the items stolen."));
            Assert.That(rig.Presenter.TakeAllButton.enabledSelf, Is.True);

            rig.Feed.RaiseLootBagContent(Content(LootBagAccess.Locked, windowMs: 30_000, rows: Row(0, 3))); // left the party
            Assert.That(rig.Presenter.TitleText, Is.EqualTo("Someone's bag (theirs for now)"));
            Assert.That(rig.Presenter.NoteText, Is.Empty);
            Assert.That((rig.Presenter.TakeAllButton.enabledSelf, rig.Presenter.Row(0).enabledSelf), Is.EqualTo((false, false)));
        }

        // ---- presenter

        [Test]
        public void The_window_says_whose_bag_it_is_without_a_name_and_marks_stolen_rows()
        {
            var rig = new Rig();
            Assert.That(rig.Presenter.IsShown, Is.False);

            rig.Feed.RaiseLootBagContent(Content(LootBagAccess.Own, rows: new[] { Row(0, 3), Row(1, 2, stolen: true) }));
            Assert.That(rig.Presenter.IsShown, Is.True);
            Assert.That(rig.Presenter.TitleText, Is.EqualTo("Your bag"));
            Assert.That(rig.Presenter.RowCount, Is.EqualTo(2));
            Assert.That(rig.Presenter.RowLabel(0), Is.EqualTo("Demon Horn x3"));
            Assert.That(rig.Presenter.RowLabel(1), Is.EqualTo("Demon Horn x2 (stolen)"));
            Assert.That(rig.Presenter.TimeText, Is.EqualTo("Crumbles in 10 min."));
            Assert.That(rig.Presenter.TakeAllButton.enabledSelf, Is.True);

            rig.Feed.RaiseLootBagContent(Content(LootBagAccess.Locked, windowMs: 42_000, rows: Row(0, 3)));
            Assert.That(rig.Presenter.TitleText, Is.EqualTo("Someone's bag (theirs for now)"));
            Assert.That(rig.Presenter.TimeText, Does.StartWith("Only its owner and their party for 42 s."));
            Assert.That((rig.Presenter.TakeAllButton.enabledSelf, rig.Presenter.Row(0).enabledSelf), Is.EqualTo((false, false)));

            rig.Feed.RaiseLootBagContent(Content(LootBagAccess.TakeStolen, rows: Row(0, 3)));
            Assert.That(rig.Presenter.NoteText, Is.EqualTo("Taking from it marks the items stolen."));
            rig.Feed.RaiseLootBagContent(Content(LootBagAccess.CollectForOwner, windowMs: 5_000, rows: Row(0, 3)));
            Assert.That(rig.Presenter.NoteText, Is.EqualTo("What you take goes to its owner by mail."));
        }

        [Test]
        public void Every_loot_bag_text_has_a_committed_string_and_the_prompt_says_open_at_a_bag()
        {
            var text = HudConfig.From(Tables()).Text;
            foreach (string id in LootBagTextIds.All)
                Assert.That(text.Get(id), Is.Not.EqualTo(id), $"UiStrings has no row '{id}'");
            Assert.That(text.Get(HudTextIds.BagPrompt, new TextArg("key", "G")), Is.EqualTo("Press G to open the bag"));
            Assert.That(text.Get(InventoryTextIds.SlotStolen), Is.Not.EqualTo(InventoryTextIds.SlotStolen));

            var prompt = new PickupPromptViewModel();
            prompt.Set(Bag, true, "G", isBag: true);
            Assert.That((prompt.Visible, prompt.IsBag), Is.EqualTo((true, true)));
        }
    }
}
