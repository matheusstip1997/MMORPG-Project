#nullable enable
using System.Collections.Generic;
using System.Linq;
using Game.Gameplay.Entities;
using Game.Gameplay.Prediction;
using Game.Gameplay.Streaming;
using Game.UI.Config;
using Game.UI.HUD;
using Game.UI.Menus.Social;
using Game.UI.ViewModels.Hud;
using Game.UI.ViewModels.Social;
using NUnit.Framework;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Actions;

namespace Game.UI.Tests
{
    /// <summary>
    /// The social window (spec 6.5 "M4 social", 9.3 SO, 9.13; WP-M4-16): <see cref="ClientSocial"/> keeps the server's lists and
    /// sends requests by ref or name; the chat commands <c>/friend</c>, <c>/unfriend</c>, <c>/block</c>, <c>/mute</c>,
    /// <c>/unblock</c>; the binder's requests, refusal and notice toasts and the trade lock; the presenter's tabs and rows (online
    /// or offline, requests with Accept and Decline, mutes and blocks); and the committed texts, reasons and key.
    /// </summary>
    public sealed class SocialWindowTests
    {
        private static ConfigSet Tables()
        {
            Assert.That(ClientConfig.TryLoad(out var set, out var error), Is.True, error);
            return set;
        }

        private static SocialState State(uint version, SocialFriendEntry[]? friends = null, SocialIncomingEntry[]? incoming = null,
            SocialBlockEntry[]? blocks = null) =>
            new SocialState
            {
                Version = version, Friends = friends ?? new SocialFriendEntry[0], Incoming = incoming ?? new SocialIncomingEntry[0],
                Blocks = blocks ?? new SocialBlockEntry[0],
            };

        // ---- ClientSocial

        [Test]
        public void Client_social_keeps_the_newest_state_and_the_last_answer_and_counts_its_requests()
        {
            var social = new ClientSocial();
            byte[] request = social.Request(SocialAction.AddFriend, 0, "Bob", out uint id);
            Assert.That(id, Is.EqualTo(1));
            Assert.That(ReliableMessages.TryRead(request, out SocialRequest read), Is.True);
            Assert.That((read.Action, read.Name), Is.EqualTo((SocialAction.AddFriend, "Bob")));

            Assert.That(social.Apply(ReliableMessages.Write(ReliableMessageId.SocialState,
                State(2, new[] { new SocialFriendEntry { Ref = 4, Name = "Bob", Online = true } })), out _), Is.True);
            social.Apply(ReliableMessages.Write(ReliableMessageId.SocialState, State(1)), out _); // older: ignored
            Assert.That(social.State!.Friends.Length, Is.EqualTo(1));
            Assert.That(social.FindFriend("bob")!.Ref, Is.EqualTo(4));
            social.Apply(ReliableMessages.Write(ReliableMessageId.SocialResult,
                new SocialResult { RequestId = 1, Action = SocialAction.AddFriend, Notice = SocialNotice.RequestSent, Name = "Bob" }), out _);
            Assert.That(social.ResultFor(1)!.Notice, Is.EqualTo(SocialNotice.RequestSent));
            Assert.That(social.Apply(ReliableMessages.Write(ReliableMessageId.MailCount, new MailCount()), out _), Is.False);

            social.Reset();
            Assert.That((social.State, social.RequestsSent), Is.EqualTo(((SocialState?)null, 0u)));
        }

        // ---- chat commands

        [TestCase("/friend Bob", ChatCommandKind.Friend)]
        [TestCase("/UNFRIEND Bob", ChatCommandKind.Unfriend)]
        [TestCase("/block Bob", ChatCommandKind.Block)]
        [TestCase("/mute Bob", ChatCommandKind.Mute)]
        [TestCase("/unblock Bob", ChatCommandKind.Unblock)]
        public void The_social_commands_name_one_player(string line, ChatCommandKind kind)
        {
            Assert.That(ChatCommandParser.TryParse(line, out var command), Is.EqualTo(ChatCommandParse.Command));
            Assert.That((command.Kind, command.Name), Is.EqualTo((kind, "Bob")));
            Assert.That(ChatCommandParser.TryParse(line.Split(' ')[0], out _), Is.EqualTo(ChatCommandParse.Usage)); // no name
            Assert.That(ChatCommandParser.TryParse(line + " extra", out _), Is.EqualTo(ChatCommandParse.Usage));
        }

        // ---- binder

        private sealed class FakeSocialRequests : ISocialRequests
        {
            public List<string> Sent { get; } = new List<string>();

            public uint RequestSocial(SocialAction action, uint reference, string name)
            {
                Sent.Add($"{action} {reference} {name}".TrimEnd());
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
                    null, null, settings.TickMs);
                Feed.SocialRequests = Requests;
                Binder = new SocialWindowBinder(SocialConfig.From(set), Model, Toast, 2_000, () => 0, readToggleKey: false);
                Binder.Bind(Feed);
            }

            public HudConfig Hud { get; }
            public WorldClientFeed Feed { get; }
            public FakeSocialRequests Requests { get; } = new FakeSocialRequests();
            public SocialWindowViewModel Model { get; } = new SocialWindowViewModel();
            public ToastViewModel Toast { get; } = new ToastViewModel();
            public SocialWindowBinder Binder { get; }
        }

        [Test]
        public void The_buttons_send_by_the_typed_name_or_the_entrys_ref_and_the_field_empties()
        {
            var rig = new Rig();
            rig.Binder.AddFriend(); // nothing typed: nothing sent
            rig.Binder.SetDraft("  Bob ");
            rig.Binder.AddFriend();
            Assert.That(rig.Model.Draft, Is.Empty);
            rig.Binder.SetDraft("Cid");
            rig.Binder.MuteTyped();
            rig.Binder.SetDraft("Dan");
            rig.Binder.BlockTyped();
            rig.Binder.Accept(3);
            rig.Binder.Decline(4);
            rig.Binder.RemoveFriend(5);
            rig.Binder.Unblock(6);
            rig.Binder.Accept(0); // no entry: nothing

            Assert.That(rig.Requests.Sent, Is.EqualTo(new[]
            {
                "AddFriend 0 Bob", "Mute 0 Cid", "Block 0 Dan", "AcceptFriend 3", "DeclineFriend 4", "RemoveFriend 5", "Unblock 6",
            }));
        }

        [Test]
        public void The_lists_come_from_the_server_and_answers_show_as_toasts()
        {
            var rig = new Rig();
            rig.Feed.RaiseSocial(State(1, incoming: new[] { new SocialIncomingEntry { Ref = 2, Name = "Bob" } }));
            Assert.That((rig.Model.HasState, rig.Model.IncomingCount), Is.EqualTo((true, 1)));

            rig.Feed.RaiseSocialAnswered(new SocialResult { RequestId = 1, Action = SocialAction.AddFriend, Reason = SocialReason.SocialTargetNotFound });
            Assert.That(rig.Toast.ReasonCode, Is.EqualTo("SocialTargetNotFound"));
            rig.Feed.RaiseSocialAnswered(new SocialResult { Notice = SocialNotice.RequestReceived, Name = "Bob" });
            Assert.That((rig.Toast.TextId, rig.Toast.ArgName, rig.Toast.ArgValue), Is.EqualTo((SocialTextIds.ToastRequestReceived, "name", "Bob")));
            rig.Feed.RaiseSocialAnswered(new SocialResult { RequestId = 2, Action = SocialAction.Block });
            Assert.That(rig.Toast.TextId, Is.EqualTo(SocialTextIds.ToastBlocked));

            rig.Feed.RaiseDisconnected("");
            Assert.That((rig.Model.HasState, rig.Model.IncomingCount), Is.EqualTo((false, 0)));
        }

        [Test]
        public void A_trade_keeps_the_window_closed_and_its_key_does_nothing_until_it_ends()
        {
            var rig = new Rig();
            bool trade = false;
            rig.Binder.TradeOpen = () => trade;
            rig.Binder.Toggle();
            Assert.That(rig.Model.Open, Is.True);
            trade = true;
            rig.Binder.Frame(16);
            Assert.That(rig.Model.Open, Is.False);
            rig.Binder.Toggle();
            Assert.That(rig.Model.Open, Is.False);
            Assert.That(rig.Toast.ReasonCode, Is.EqualTo(nameof(ClientReasonCode.WindowBlockedByTrade))); // WP-M4-11: and says why
            trade = false;
            rig.Binder.Toggle();
            Assert.That(rig.Model.Open, Is.True);
        }

        [Test]
        public void The_name_field_suspends_the_gameplay_keys_while_it_has_the_focus()
        {
            var rig = new Rig();
            rig.Binder.Toggle();
            rig.Binder.FocusField(true);
            Assert.That(rig.Feed.WindowTextInput, Is.True);
            rig.Binder.CloseWindow();
            Assert.That(rig.Feed.WindowTextInput, Is.False);
        }

        // ---- presenter

        [Test]
        public void The_friends_tab_lists_requests_then_friends_online_or_offline_and_the_blocked_tab_the_blocks()
        {
            var rig = new Rig();
            var presenter = new SocialWindowPresenter(rig.Hud, new HudView(rig.Hud), rig.Model, rig.Binder);
            presenter.Bind();
            rig.Model.SetOpen(true);
            Assert.That(presenter.EmptyShown, Is.True); // nothing yet

            rig.Feed.RaiseSocial(State(1,
                new[] { new SocialFriendEntry { Ref = 1, Name = "Ann", Online = true }, new SocialFriendEntry { Ref = 2, Name = "Bob" } },
                new[] { new SocialIncomingEntry { Ref = 3, Name = "Cid" } },
                new[] { new SocialBlockEntry { Ref = 4, Name = "Dan", Kind = SocialBlockKind.Block }, new SocialBlockEntry { Ref = 5, Name = "Eve", Kind = SocialBlockKind.Mute } }));

            Assert.That(presenter.RowTexts, Is.EqualTo(new[] { "Cid wants to be your friend", "Ann - Online", "Bob - Offline" }));
            Assert.That(presenter.EmptyShown, Is.False);
            Assert.That(presenter.ListButton("social_accept_3"), Is.Not.Null);
            Assert.That(presenter.ListButton("social_remove_2"), Is.Not.Null);
            Assert.That(presenter.AddButton.enabledSelf, Is.False); // nothing typed

            rig.Binder.SelectTab(SocialTab.Blocked);
            Assert.That(presenter.RowTexts, Is.EqualTo(new[] { "Dan - Blocked", "Eve - Muted" }));
            Assert.That(presenter.ListButton("social_unblock_5"), Is.Not.Null);
            rig.Binder.SetDraft("Fay");
            Assert.That((presenter.BlockButton.enabledSelf, presenter.MuteButton.enabledSelf), Is.EqualTo((true, true)));
        }

        // ---- committed texts

        [Test]
        public void Committed_strings_cover_every_social_text_and_reason_and_the_key_is_l_with_an_so_button()
        {
            var set = Tables();
            var ui = new HashSet<string>(set.TryGet(typeof(UiStrings), out var table) ? table.Rows.Cast<UiStrings>().Select(r => r.Id) : new string[0]);
            var reasons = new HashSet<string>(set.TryGet(typeof(ReasonCodeStrings), out var codes) ? codes.Rows.Cast<ReasonCodeStrings>().Select(r => r.Code) : new string[0]);

            Assert.That(SocialTextIds.All.Where(id => !ui.Contains(id)), Is.Empty);
            Assert.That(System.Enum.GetNames(typeof(SocialReason)).Where(n => n != "None" && !reasons.Contains(n)), Is.Empty);
            Assert.That(reasons.Contains(nameof(ChatReason.ChatTargetBlocked)), Is.True);
            Assert.That(SocialConfig.From(set).ToggleBinding, Is.EqualTo("<Keyboard>/l"));
            Assert.That(ui.Contains(HudTextIds.ShortcutLabel("social")) && ui.Contains(HudTextIds.ShortcutName("social")), Is.True);
            var hud = HudConfig.From(set);
            Assert.That(hud.TryGetLayout(HudElementIds.ShortcutRow, out var row) && row.Buttons.Contains("social"), Is.True);
            Assert.That(hud.TryGetLayout(HudElementIds.SocialWindow, out _), Is.True);
        }
    }
}
