#nullable enable
using System.Collections.Generic;
using System.Linq;
using Game.Gameplay.Entities;
using Game.Gameplay.Prediction;
using Game.Gameplay.Streaming;
using Game.UI.Config;
using Game.UI.HUD;
using Game.UI.Menus.Mail;
using Game.UI.ViewModels.Hud;
using Game.UI.ViewModels.Mail;
using NUnit.Framework;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Actions;

namespace Game.UI.Tests
{
    /// <summary>
    /// The mail window (spec 4.5, 9.3, 9.13; WP-M4-3): the view model's list, selection, the one pending claim and the badge
    /// count only from the server; the binder's requests (the list on open, a mail's content, Take and Take all), refusal and
    /// news toasts and the list again after an applied claim; the presenter's rows, subjects with typed arguments, the full line
    /// and the disabled buttons; and the committed texts, subjects, reasons and key.
    /// </summary>
    public sealed class MailWindowTests
    {
        private const ushort Ash = 2; // ItemDef m1.demonAsh

        private static ConfigSet Tables()
        {
            Assert.That(ClientConfig.TryLoad(out var set, out var error), Is.True, error);
            return set;
        }

        private static MailHeader Mail(long id, long gold = 0, int ash = 0, MailKind kind = MailKind.ListingProceeds, string? subject = null,
            params MailArgEntry[] args) =>
            new MailHeader
            {
                MailId = id, Kind = kind, SubjectId = subject ?? "mail.subject.listingProceeds", Args = args, Gold = gold,
                CreatedUtcMs = 0, ExpiresUtcMs = 30L * 86_400_000L,
                Items = ash > 0 ? new[] { new MailAttachmentEntry { ItemNetId = Ash, Count = (ushort)ash } } : new MailAttachmentEntry[0],
            };

        private static MailList List(uint requestId, params MailHeader[] mails) =>
            new MailList { RequestId = requestId, Total = (ushort)mails.Length, Mails = mails };

        // ---- view model

        [Test]
        public void The_badge_comes_only_from_the_servers_count_and_a_claim_waits_for_it()
        {
            var model = new MailWindowViewModel();
            model.SetCount(new MailCount { Open = 2, Cap = 50 });
            model.SetList(List(1, Mail(10, gold: 5), Mail(11, ash: 3)));
            model.Select(10);
            model.SetPendingClaim(4);

            Assert.That(model.CanClaim, Is.False); // one claim at a time
            Assert.That(model.OnResult(new MailResult { RequestId = 3 }, out _), Is.Null); // not ours
            Assert.That(model.OnResult(new MailResult { RequestId = 4, MailsEmptied = 1, GoldTaken = 5 }, out bool applied), Is.Null);
            Assert.That((applied, model.OpenCount), Is.EqualTo((true, 2))); // no local count down: the server's next count
            model.SetCount(new MailCount { Open = 1, Cap = 50 });
            Assert.That(model.OpenCount, Is.EqualTo(1));
        }

        [Test]
        public void A_refusal_returns_its_code_and_a_selection_whose_mail_is_gone_is_cleared()
        {
            var model = new MailWindowViewModel();
            model.SetList(List(1, Mail(10), Mail(11)));
            Assert.That(model.Select(11), Is.True);
            model.SetPendingClaim(2);
            Assert.That(model.OnResult(new MailResult { RequestId = 2, Reason = MailReason.BagFull }, out bool applied), Is.EqualTo("BagFull"));
            Assert.That(applied, Is.False);

            model.SetList(List(3, Mail(10)));
            Assert.That((model.Selected, model.SelectedMail), Is.EqualTo((0L, (MailHeader?)null)));
            Assert.That(model.SetList(new MailList { RequestId = 4, Reason = MailReason.MailRateLimited }), Is.EqualTo("MailRateLimited"));
            Assert.That(model.Mails.Count, Is.EqualTo(1)); // a refused list keeps the last one
        }

        [Test]
        public void The_mailbox_is_full_at_the_cap()
        {
            var model = new MailWindowViewModel();
            model.SetCount(new MailCount { Open = 49, Cap = 50 });
            Assert.That(model.IsFull, Is.False);
            model.SetCount(new MailCount { Open = 51, Cap = 50 }); // settlements arrive over the cap
            Assert.That(model.IsFull, Is.True);
        }

        // ---- binder

        private sealed class FakeMailRequests : IMailRequests
        {
            public List<string> Sent { get; } = new List<string>();

            public uint RequestMailList() => Add("list");

            public uint OpenMail(long mailId) => Add("open " + mailId);

            public uint ClaimMail(long mailId) => Add("claim " + mailId);

            private uint Add(string what)
            {
                Sent.Add(what);
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
                Hud = HudConfig.From(set);
                Config = MailConfig.From(set);
                Feed = MailWindowTests.Feed(set);
                Feed.MailRequests = Requests;
                Binder = new MailWindowBinder(Config, Hud.Text, Model, Toast, 2_000, () => Now, readToggleKey: false);
                Binder.Bind(Feed);
            }

            public HudConfig Hud { get; }
            public MailConfig Config { get; }
            public WorldClientFeed Feed { get; }
            public FakeMailRequests Requests { get; } = new FakeMailRequests();
            public MailWindowViewModel Model { get; } = new MailWindowViewModel();
            public ToastViewModel Toast { get; } = new ToastViewModel();
            public MailWindowBinder Binder { get; }
            public double Now { get; set; }
        }

        [Test]
        public void Opening_asks_for_the_list_and_selecting_a_mail_asks_for_its_content()
        {
            var rig = new Rig();
            rig.Binder.Frame(0);
            Assert.That(rig.Requests.Sent, Is.Empty); // closed

            rig.Model.SetOpen(true);
            rig.Binder.Frame(16);
            rig.Binder.Frame(32); // once per opening
            rig.Feed.RaiseMailList(List(1, Mail(10, gold: 5)));
            rig.Binder.SelectMail(10);

            Assert.That(rig.Requests.Sent, Is.EqualTo(new[] { "list", "open 10" }));
        }

        [Test]
        public void A_trade_keeps_the_window_closed_and_m_does_nothing_until_it_ends()
        {
            var rig = new Rig();
            bool trade = false;
            rig.Binder.TradeOpen = () => trade; // placeholder rule (no spec 9.13 rule): the trade window takes the left edge
            rig.Model.SetOpen(true);
            rig.Binder.Frame(0);

            trade = true;
            rig.Binder.Frame(16);
            Assert.That(rig.Model.Open, Is.False);
            rig.Binder.Toggle(); // M or the M button during the trade
            rig.Binder.Frame(32);
            Assert.That(rig.Model.Open, Is.False);
            rig.Model.SetOpen(true); // anything else that opens it
            rig.Binder.Frame(48);
            Assert.That(rig.Model.Open, Is.False);

            trade = false;
            rig.Binder.Toggle();
            rig.Binder.Frame(64);
            Assert.That(rig.Model.Open, Is.True);
        }

        [Test]
        public void Take_and_take_all_send_one_claim_at_a_time_and_an_applied_claim_lists_again_with_a_news_toast()
        {
            var rig = new Rig();
            rig.Model.SetOpen(true);
            rig.Binder.Frame(0);
            rig.Feed.RaiseMailList(List(1, Mail(10, gold: 50, ash: 3)));
            rig.Binder.SelectMail(10);

            rig.Binder.Claim();
            rig.Binder.ClaimAll(); // one claim at a time
            Assert.That(rig.Requests.Sent, Is.EqualTo(new[] { "list", "open 10", "claim 10" }));

            rig.Feed.RaiseMailAnswered(new MailResult
            {
                RequestId = 3, MailId = 10, MailsEmptied = 1, GoldTaken = 50,
                Taken = new[] { new MailAttachmentEntry { ItemNetId = Ash, Count = 3 } },
            });
            Assert.That(rig.Toast.Visible, Is.True);
            Assert.That((rig.Toast.TextId, rig.Toast.ArgName), Is.EqualTo((MailTextIds.ToastClaimed, "what")));
            Assert.That(rig.Toast.ArgValue, Is.EqualTo("Demon Ash x3, 50 gold"));
            Assert.That(rig.Requests.Sent.Last(), Is.EqualTo("list"));

            rig.Feed.RaiseMailList(List(5, Mail(11)));
            rig.Binder.ClaimAll();
            Assert.That(rig.Requests.Sent.Last(), Is.EqualTo("claim 0"));
            rig.Feed.RaiseMailAnswered(new MailResult { RequestId = 5, Reason = MailReason.BagFull }); // the fifth request
            Assert.That(rig.Toast.ReasonCode, Is.EqualTo("BagFull"));
        }

        [Test]
        public void A_count_change_while_open_lists_again_and_a_disconnect_clears_the_window()
        {
            var rig = new Rig();
            rig.Feed.RaiseMailCount(new MailCount { Open = 1, Cap = 50 });
            Assert.That(rig.Model.OpenCount, Is.EqualTo(1)); // the badge works with the window closed
            rig.Model.SetOpen(true);
            rig.Binder.Frame(0);
            rig.Feed.RaiseMailList(List(1, Mail(10)));
            rig.Feed.RaiseMailCount(new MailCount { Open = 2, Cap = 50 }); // a mail arrived
            Assert.That(rig.Requests.Sent, Is.EqualTo(new[] { "list", "list" }));

            rig.Feed.RaiseDisconnected("Disconnected");
            Assert.That((rig.Model.Open, rig.Model.OpenCount, rig.Model.Mails.Count), Is.EqualTo((false, 0, 0)));
        }

        [Test]
        public void An_applied_claim_lists_once_and_the_count_after_it_does_not_list_again()
        {
            var rig = new Rig();
            rig.Model.SetOpen(true);
            rig.Binder.Frame(0);
            rig.Feed.RaiseMailCount(new MailCount { Open = 1, Cap = 50 });
            rig.Feed.RaiseMailList(List(1, Mail(10, gold: 50)));
            rig.Binder.SelectMail(10);
            rig.Binder.Claim();
            rig.Feed.RaiseMailAnswered(new MailResult { RequestId = 3, MailId = 10, MailsEmptied = 1, GoldTaken = 50 });
            rig.Feed.RaiseMailCount(new MailCount { Open = 0, Cap = 50 }); // the server's new count follows the result

            Assert.That(rig.Requests.Sent, Is.EqualTo(new[] { "list", "open 10", "claim 10", "list" })); // 3 per mail, one list
            rig.Feed.RaiseMailList(List(4));
            Assert.That(rig.Model.Mails.Count, Is.EqualTo(0));
        }

        [Test]
        public void A_refused_list_shows_in_the_window_and_is_asked_again_once_after_the_rate_limit_period()
        {
            var rig = new Rig();
            var presenter = new MailWindowPresenter(rig.Hud, new HudView(rig.Hud), rig.Config, rig.Model, rig.Binder, () => 0);
            presenter.Bind();
            rig.Model.SetOpen(true);
            rig.Binder.Frame(0);
            rig.Feed.RaiseMailList(new MailList { RequestId = 1, Reason = MailReason.InventoryUnavailable }); // the mailbox still loading
            Assert.That((presenter.UnavailableShown, presenter.EmptyShown), Is.EqualTo((true, false)));
            Assert.That(rig.Toast.ReasonCode, Is.EqualTo("InventoryUnavailable"));

            rig.Now = rig.Config.ListRetryMs - 1;
            rig.Binder.Frame(rig.Now);
            Assert.That(rig.Requests.Sent, Is.EqualTo(new[] { "list" })); // not before the period
            rig.Now = rig.Config.ListRetryMs;
            rig.Binder.Frame(rig.Now);
            Assert.That(rig.Requests.Sent, Is.EqualTo(new[] { "list", "list" }));

            rig.Feed.RaiseMailList(new MailList { RequestId = 2, Reason = MailReason.MailRateLimited }); // refused again: no loop
            for (int i = 1; i <= 5; i++)
                rig.Binder.Frame(rig.Now + i * rig.Config.ListRetryMs);
            Assert.That(rig.Requests.Sent.Count, Is.EqualTo(2));

            rig.Binder.CloseMail();
            rig.Binder.Frame(rig.Now + 10 * rig.Config.ListRetryMs);
            rig.Binder.Toggle(); // reopening asks again
            rig.Binder.Frame(rig.Now + 11 * rig.Config.ListRetryMs);
            Assert.That(rig.Requests.Sent.Count, Is.EqualTo(3));
            rig.Feed.RaiseMailList(List(3, Mail(10)));
            Assert.That((presenter.UnavailableShown, presenter.RowCount), Is.EqualTo((false, 1)));
        }

        [Test]
        public void A_refused_reload_keeps_the_rows_says_so_and_a_count_change_lists_again()
        {
            var rig = new Rig();
            rig.Model.SetOpen(true);
            rig.Binder.Frame(0);
            rig.Feed.RaiseMailList(List(1, Mail(10), Mail(11)));
            rig.Feed.RaiseMailCount(new MailCount { Open = 2, Cap = 50 }); // a change: the second list
            rig.Feed.RaiseMailList(new MailList { RequestId = 2, Reason = MailReason.MailRateLimited });
            Assert.That((rig.Model.ListRefused, rig.Model.Mails.Count), Is.EqualTo((true, 2)));

            rig.Feed.RaiseMailCount(new MailCount { Open = 1, Cap = 50 }); // the next change asks again
            Assert.That(rig.Requests.Sent, Is.EqualTo(new[] { "list", "list", "list" }));
        }

        [Test]
        public void The_claim_toast_names_the_first_items_and_how_many_more()
        {
            var rig = new Rig();
            var taken = new MailResult
            {
                GoldTaken = 50,
                Taken = Enumerable.Range(0, 5).Select(_ => new MailAttachmentEntry { ItemNetId = Ash, Count = 1 }).ToArray(),
            };
            Assert.That(rig.Config.ToastItemsShown, Is.EqualTo(3));
            Assert.That(MailWindowBinder.Taken(rig.Config, rig.Hud.Text, taken),
                Is.EqualTo("Demon Ash x1, Demon Ash x1, Demon Ash x1 and 3 more"));
            taken.Taken = taken.Taken.Take(2).ToArray();
            Assert.That(MailWindowBinder.Taken(rig.Config, rig.Hud.Text, taken), Is.EqualTo("Demon Ash x1, Demon Ash x1, 50 gold"));
        }

        // ---- presenter

        private static MailWindowPresenter Presenter(out MailWindowViewModel model, out MailWindowBinder binder, System.Func<long>? utcNowMs = null)
        {
            var set = Tables();
            var hud = HudConfig.From(set);
            var mail = MailConfig.From(set);
            model = new MailWindowViewModel();
            binder = new MailWindowBinder(mail, hud.Text, model, new ToastViewModel(), 1500, () => 0, readToggleKey: false);
            var feed = Feed(set);
            feed.MailRequests = new FakeMailRequests();
            binder.Bind(feed);
            var presenter = new MailWindowPresenter(hud, new HudView(hud), mail, model, binder, utcNowMs ?? (() => 0));
            presenter.Bind();
            return presenter;
        }

        [Test]
        public void The_window_lists_the_mails_with_their_subjects_gold_and_expiry_and_the_selected_mails_contents()
        {
            var presenter = Presenter(out var model, out var binder);
            Assert.That(presenter.IsShown, Is.False);

            model.SetCount(new MailCount { Open = 2, Cap = 50 });
            model.SetList(new MailList
            {
                RequestId = 1, Total = 3,
                Mails = new[] { Mail(10, gold: 50), Mail(11, ash: 3, kind: MailKind.AdminCompensation, subject: "mail.subject.adminCompensation") },
            });
            model.SetOpen(true);

            Assert.That(presenter.IsShown, Is.True);
            Assert.That(presenter.CountText, Is.EqualTo("2 of 50 mails"));
            Assert.That(presenter.RowCount, Is.EqualTo(2));
            Assert.That(presenter.RowLabel(0), Is.EqualTo("Your listing sold   50 gold   Expires in 30 d"));
            Assert.That(presenter.RowLabel(1), Does.StartWith("A gift from the game masters"));
            Assert.That(presenter.ClaimButton.enabledSelf, Is.False); // nothing selected
            Assert.That(presenter.ClaimAllButton.enabledSelf, Is.True);

            binder.SelectMail(11);
            Assert.That(presenter.DetailText, Does.Contain("x3"));
            Assert.That(presenter.ClaimButton.enabledSelf, Is.True);
            model.SetPendingClaim(9);
            Assert.That((presenter.ClaimButton.enabledSelf, presenter.ClaimAllButton.enabledSelf), Is.EqualTo((false, false)));
            Assert.That(presenter.FullShown, Is.False);
            model.SetCount(new MailCount { Open = 50, Cap = 50 });
            Assert.That(presenter.FullShown, Is.True);
        }

        [Test]
        public void A_subject_fills_its_typed_arguments_and_shows_no_markup()
        {
            var presenter = Presenter(out _, out _);
            var header = Mail(1, subject: "mail.count", args: new[]
            {
                new MailArgEntry { Name = "open", Kind = MailArgKind.Number, Value = 7 },
                new MailArgEntry { Name = "cap", Kind = MailArgKind.Item, Value = Ash },
            });

            Assert.That(presenter.Subject(header), Is.EqualTo("7 of Demon Ash mails"));
        }

        [Test]
        public void The_detail_draws_the_mails_content_with_enchant_quality_gems_rarity_and_the_stolen_mark()
        {
            var set = Tables();
            Assert.That(set.TryGet(typeof(ItemDef), out var table), Is.True);
            ushort Net(string id) => (ushort)table.Rows.Cast<ItemDef>().Single(i => i.Id == id).NetId;
            var presenter = Presenter(out var model, out var binder);
            ushort sword = Net("m2.trainingSword"), ruby = Net("m3.rubyT1");
            var header = Mail(12, gold: 5);
            header.Items = new[] { new MailAttachmentEntry { ItemNetId = sword, Count = 1, Rarity = 2, Stolen = true } };
            model.SetList(List(1, header));
            model.SetOpen(true);
            binder.SelectMail(12);

            string before = presenter.DetailText; // the header until the content came: rarity and the stolen mark
            Assert.That(before, Does.Contain("Training Sword x1 (Rare, Stolen)"));
            Assert.That(before, Does.Contain("5 gold"));

            model.SetContent(new MailContent
            {
                RequestId = 2, Header = header,
                Items = new[]
                {
                    new InventorySlotEntry { ItemNetId = sword, Count = 1, Rarity = 2, Stolen = true, Enchant = 7, Quality = 80, Gems = new[] { ruby } },
                },
            });
            string after = presenter.DetailText;
            Assert.That(after, Does.Contain("Training Sword +7 x1 (Rare, Stolen, Quality 80, Gem: "));
            Assert.That(after.Split('\n').Length, Is.EqualTo(3)); // subject, the sword, the gold
        }

        [Test]
        public void An_open_window_counts_its_expiry_down_on_the_refresh()
        {
            long now = 0;
            var presenter = Presenter(out var model, out var binder, () => now);
            model.SetList(List(1, Mail(10, gold: 50)));
            model.SetOpen(true);
            binder.Frame(0);
            Assert.That(presenter.RowLabel(0), Does.EndWith("Expires in 30 d"));

            now = 2L * 86_400_000L; // two days later, nothing else changed
            int refreshMs = MailConfig.From(Tables()).WindowRefreshMs;
            binder.Frame(refreshMs - 1);
            Assert.That(presenter.RowLabel(0), Does.EndWith("Expires in 30 d"));
            binder.Frame(refreshMs);
            Assert.That(presenter.RowLabel(0), Does.EndWith("Expires in 28 d"));
        }

        // ---- committed texts

        [Test]
        public void Committed_ui_strings_cover_every_mail_text_and_subject_and_reason_and_the_toggle_key_is_m()
        {
            var set = Tables();
            var ui = new HashSet<string>(set.TryGet(typeof(UiStrings), out var table) ? table.Rows.Cast<UiStrings>().Select(r => r.Id) : new string[0]);
            var reasons = new HashSet<string>(set.TryGet(typeof(ReasonCodeStrings), out var codes) ? codes.Rows.Cast<ReasonCodeStrings>().Select(r => r.Code) : new string[0]);

            Assert.That(MailTextIds.All.Where(id => !ui.Contains(id)), Is.Empty);
            Assert.That(MailTextIds.Subjects.Where(id => !ui.Contains(id)), Is.Empty);
            Assert.That(MailTextIds.Subjects.Length, Is.EqualTo((int)ReliableMessages.LastMailKind)); // one per MailKind
            Assert.That(System.Enum.GetNames(typeof(MailReason)).Where(n => n != "None" && !reasons.Contains(n)), Is.Empty);
            Assert.That(MailConfig.From(set).ToggleBinding, Is.EqualTo("<Keyboard>/m"));
        }
    }
}
