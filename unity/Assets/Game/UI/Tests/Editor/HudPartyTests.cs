#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Game.Gameplay.Entities;
using Game.UI.Config;
using Game.UI.HUD;
using Game.UI.ViewModels.Hud;
using NUnit.Framework;
using Shared.Protocol;

namespace Game.UI.Tests
{
    /// <summary>
    /// The party on the HUD and the chat channels (WP-M4-2, spec 6.5, 9.3, 9.12): the frame lists the other members with the
    /// leader's buttons, each change shows its toast, the invite toast counts down and answers once; the chat tabs show their
    /// channel and send on it, the player commands become party requests or channel lines, and other <c>/</c> lines go out as
    /// typed for the dev console.
    /// </summary>
    public sealed class HudPartyTests
    {
        private sealed class Requests : IPartyRequests
        {
            public List<(PartyAction Action, uint InviteId, uint Entity, string Name)> Sent { get; } = new();

            public uint RequestParty(PartyAction action, uint inviteId = 0, uint targetEntityId = 0, string targetName = "")
            {
                Sent.Add((action, inviteId, targetEntityId, targetName));
                return (uint)Sent.Count;
            }
        }

        private static HudConfig Committed()
        {
            Assert.That(ClientConfig.TryLoad(out var set, out var error), Is.True, error);
            return HudConfig.From(set);
        }

        private static PartyState State(uint version, uint leader, PartyChangeKind cause = PartyChangeKind.None, string causeName = "",
            params (uint Id, string Name, bool Online)[] members) => new PartyState
        {
            Version = version, PartyId = members.Length == 0 ? 0 : 5, LeaderEntityId = leader, Cause = cause, CauseName = causeName,
            Members = members.Select(m => new PartyMemberState { EntityId = m.Id, Name = m.Name, Level = 4, Online = m.Online, HpPercent = 60 }).ToArray(),
        };

        [Test]
        public void The_frame_lists_the_other_members_and_the_leader_gets_the_buttons()
        {
            var hud = Committed();
            var frame = new PartyFrameViewModel();
            var binder = new PartyBinder(frame, new PartyInviteViewModel(), new ToastViewModel(), 1_000, () => 0);
            var presenter = new PartyFramePresenter(hud, new HudView(hud), frame, binder);
            presenter.Bind();
            Assert.That(presenter.Visible, Is.False);

            frame.Set(State(1, 1, members: new[] { (1u, "Me", true), (2u, "Bo", true), (3u, "Cy", false) }), ownEntityId: 1);

            Assert.That(presenter.Visible, Is.True);
            Assert.That(frame.Rows.Select(r => r.EntityId), Is.EqualTo(new[] { 2u, 3u }));
            Assert.That(presenter.ShownRows()[1], Does.Contain(hud.Text.Get(HudTextIds.PartyOffline)));
            Assert.That(presenter.ButtonsOf(0).Kick.style.display.value, Is.EqualTo(UnityEngine.UIElements.DisplayStyle.Flex));

            frame.Set(State(2, 2, members: new[] { (1u, "Me", true), (2u, "Bo", true) }), ownEntityId: 1);
            Assert.That(presenter.ShownRows()[0], Does.Contain(hud.Text.Get(HudTextIds.PartyLeader)));
            Assert.That(presenter.ButtonsOf(0).Kick.style.display.value, Is.EqualTo(UnityEngine.UIElements.DisplayStyle.None));
            presenter.Dispose();
        }

        [Test]
        public void Each_change_shows_its_toast_and_the_buttons_send_by_entity()
        {
            var feed = HudChatTests.Feed();
            var requests = new Requests();
            feed.PartyRequests = requests;
            var frame = new PartyFrameViewModel();
            var toast = new ToastViewModel();
            var binder = new PartyBinder(frame, new PartyInviteViewModel(), toast, 1_000, () => 0);
            binder.Bind(feed);

            feed.RaiseParty(State(1, 2, PartyChangeKind.Left, "Al", (2, "Bo", true), (3, "Cy", true)));
            Assert.That((toast.TextId, toast.ArgValue), Is.EqualTo((HudTextIds.PartyChangeLeft, "Al")));
            feed.RaiseParty(State(2, 2, PartyChangeKind.LeaderChanged, "Bo", (2, "Bo", true), (3, "Cy", true)));
            Assert.That((toast.TextId, toast.ArgValue), Is.EqualTo((HudTextIds.PartyChangeLeaderChanged, "Bo")));
            feed.RaisePartyAnswered(new PartyResult { RequestId = 0, Action = PartyAction.Invite, Reason = PartyReason.PartyFull });
            Assert.That(toast.ReasonCode, Is.EqualTo(nameof(PartyReason.PartyFull)));

            binder.Kick(3);
            binder.PassLead(2);
            binder.Leave();
            Assert.That(requests.Sent, Is.EqualTo(new[]
            {
                (PartyAction.Kick, 0u, 3u, ""), (PartyAction.PassLead, 0u, 2u, ""), (PartyAction.Leave, 0u, 0u, ""),
            }));
            binder.Dispose();
        }

        [Test]
        public void The_invite_toast_counts_down_answers_once_and_lapses()
        {
            var hud = Committed();
            var feed = HudChatTests.Feed();
            var requests = new Requests();
            feed.PartyRequests = requests;
            double now = 0;
            var invite = new PartyInviteViewModel();
            var binder = new PartyBinder(new PartyFrameViewModel(), invite, new ToastViewModel(), 1_000, () => now);
            binder.Bind(feed);
            var presenter = new PartyInvitePresenter(hud, new HudView(hud), invite, binder);
            presenter.Bind();

            feed.RaisePartyInvite(new PartyInvite { InviteId = 9, InviterName = "Bo", InviterEntityId = 2, TimeoutMs = 3_000 });
            Assert.That(presenter.Text, Does.Contain("Bo").And.Contain("3"));
            now = 1_500;
            binder.Frame(now);
            Assert.That(invite.SecondsLeft, Is.EqualTo(2));

            binder.Accept();
            binder.Accept(); // the toast closed: a second click sends nothing
            Assert.That(requests.Sent, Is.EqualTo(new[] { (PartyAction.Accept, 9u, 0u, "") }));
            Assert.That(presenter.Text, Is.Empty);

            feed.RaisePartyInvite(new PartyInvite { InviteId = 10, InviterName = "Cy", TimeoutMs = 1_000 });
            now = 2_600;
            binder.Frame(now);
            Assert.That(invite.Visible, Is.False);
            presenter.Dispose();
            binder.Dispose();
        }

        [Test]
        public void The_tabs_show_their_channel_and_mark_unread_lines()
        {
            var chat = new ChatViewModel(10, 200);
            chat.AddLine(1, "Al", "local", ChatChannel.Local, "");
            chat.AddLine(2, "Bo", "party", ChatChannel.Party, "");
            chat.AddRefusal(nameof(ChatReason.ChatRateLimited));

            Assert.That(chat.TabLines().Select(l => l.Text), Is.EqualTo(new[] { "local", nameof(ChatReason.ChatRateLimited) }));
            Assert.That(chat.HasUnread(ChatChannel.Party), Is.True);
            chat.SelectTab(ChatChannel.Party);
            Assert.That(chat.TabLines().Select(l => l.Text), Is.EqualTo(new[] { "party", nameof(ChatReason.ChatRateLimited) }));
            Assert.That(chat.HasUnread(ChatChannel.Party), Is.False);

            chat.AddLine(3, "Cy", "psst", ChatChannel.Whisper, "");
            Assert.That(chat.WhisperPartner, Is.EqualTo("Cy"));
            chat.AddLine(1, "Me", "hi", ChatChannel.Whisper, "Dee"); // the echo of an own whisper names its recipient
            Assert.That(chat.WhisperPartner, Is.EqualTo("Dee"));
        }

        [Test]
        public void Commands_become_requests_or_channel_lines_and_other_slash_lines_go_out_as_typed()
        {
            var config = Committed();
            var models = new HudViewModels(4, 4, config.Chat.HistoryLines, config.Chat.MaxChars);
            var feed = HudChatTests.Feed();
            var requests = new Requests();
            feed.PartyRequests = requests;
            var sent = new List<(string Text, ChatChannel Channel, string Target)>();
            feed.ChatSender = (text, channel, target) =>
            {
                sent.Add((text, channel, target));
                return true;
            };
            var binder = new HudFeedBinder(config, models, () => 0);
            binder.Bind(feed);

            binder.SubmitChat("/invite Bo");
            binder.SubmitChat("/kick Bo");
            binder.SubmitChat("/lead Bo");
            binder.SubmitChat("/leave");
            binder.SubmitChat("/w Bo see you there");
            binder.SubmitChat("/p pull now");
            binder.SubmitChat("/help");
            binder.SubmitChat("/w Bo");
            models.Chat.SelectTab(ChatChannel.Party);
            binder.SubmitChat("on the party tab");

            Assert.That(requests.Sent, Is.EqualTo(new[]
            {
                (PartyAction.Invite, 0u, 0u, "Bo"), (PartyAction.Kick, 0u, 0u, "Bo"), (PartyAction.PassLead, 0u, 0u, "Bo"),
                (PartyAction.Leave, 0u, 0u, ""),
            }));
            Assert.That(sent, Is.EqualTo(new[]
            {
                ("see you there", ChatChannel.Whisper, "Bo"), ("pull now", ChatChannel.Party, ""), ("/help", ChatChannel.Local, ""),
                ("on the party tab", ChatChannel.Party, ""),
            }));
            Assert.That(models.Chat.Lines.Single(l => l.Kind == ChatLineKind.Refusal).Text, Is.EqualTo(nameof(ClientReasonCode.ChatCommandUsage)));
            binder.Dispose();
        }

        private sealed class Trades : ITradeRequests
        {
            public List<uint> Invited { get; } = new();

            public uint InviteTrade(uint targetEntityId)
            {
                Invited.Add(targetEntityId);
                return (uint)Invited.Count;
            }
        }

        /// <summary>The own player (1) and another (2) in view, from a snapshot as the server writes it.</summary>
        private static void InView(WorldClientFeed feed)
        {
            var buffer = new byte[256];
            var writer = new SnapshotWriter(buffer, new SnapshotHeader { ServerTick = 100 });
            Assert.That(writer.TryAddSpawn(new SpawnEvent { EntityId = 1, Kind = EntityKind.Player, DefId = 1 }), Is.True);
            Assert.That(writer.TryAddSpawn(new SpawnEvent { EntityId = 2, Kind = EntityKind.Player, DefId = 1 }), Is.True);
            Assert.That(writer.TryAddEntity(new EntitySnapshot { EntityId = 1, HpBucket = Snapshot.TopHpBucket }), Is.True);
            Assert.That(writer.TryAddEntity(new EntitySnapshot { EntityId = 2, XCm = 200, HpBucket = Snapshot.TopHpBucket }), Is.True);
            int size = writer.Finish();
            Assert.That(feed.Registry.ApplySnapshot(buffer.AsSpan(0, size), out _, out _, out _), Is.True);
            feed.Identities.Set(1, "Me");
            feed.Identities.Set(2, "Bo");
            feed.Identities.Set(9, "Gone"); // a name kept after its entity left view
        }

        [Test]
        public void Trade_is_a_player_command_on_the_client_path_in_every_build()
        {
            // PR #170 review: /trade, /p and /w are read here, before anything is sent, so a release build, a dev build, a dev
            // account and any other account behave the same; the server reads no player command from chat.
            var config = Committed();
            var models = new HudViewModels(4, 4, config.Chat.HistoryLines, config.Chat.MaxChars);
            var feed = HudChatTests.Feed();
            InView(feed);
            var trades = new Trades();
            feed.TradeRequests = trades;
            var sent = new List<(string Text, ChatChannel Channel, string Target)>();
            feed.ChatSender = (text, channel, target) =>
            {
                sent.Add((text, channel, target));
                return true;
            };
            var binder = new HudFeedBinder(config, models, () => 0);
            binder.Bind(feed);

            binder.SubmitChat("/trade bo");
            binder.SubmitChat("/trade Gone");
            binder.SubmitChat("/trade Me");
            binder.SubmitChat("/trade");
            binder.SubmitChat("/p ready");
            binder.SubmitChat("/w Bo ready");

            Assert.That(trades.Invited, Is.EqualTo(new[] { 2u }));
            Assert.That(models.Chat.Lines.Where(l => l.Kind == ChatLineKind.Refusal).Select(l => l.Text), Is.EqualTo(new[]
            {
                nameof(TradeReason.TradeTargetNotFound), nameof(TradeReason.TradeTargetNotFound), nameof(ClientReasonCode.ChatCommandUsage),
            }));
            Assert.That(sent, Is.EqualTo(new[] { ("ready", ChatChannel.Party, ""), ("ready", ChatChannel.Whisper, "Bo") }));
            Assert.That(sent.Any(s => s.Text.StartsWith("/")), Is.False, "no player command reaches the server as a chat line");
            binder.Dispose();
        }

        [Test]
        public void A_plain_line_on_the_whisper_tab_needs_a_partner_then_goes_to_it()
        {
            var config = Committed();
            var models = new HudViewModels(4, 4, config.Chat.HistoryLines, config.Chat.MaxChars);
            var feed = HudChatTests.Feed();
            var sent = new List<(string Text, ChatChannel Channel, string Target)>();
            feed.ChatSender = (text, channel, target) =>
            {
                sent.Add((text, channel, target));
                return true;
            };
            var binder = new HudFeedBinder(config, models, () => 0);
            binder.Bind(feed);
            models.Chat.SelectTab(ChatChannel.Whisper);

            binder.SubmitChat("anyone?");
            Assert.That(sent, Is.Empty);
            Assert.That(models.Chat.Lines.Last().Text, Is.EqualTo(nameof(ClientReasonCode.ChatNoWhisperTarget)));

            feed.RaiseChatLine(4, "Bo", "hey", ChatChannel.Whisper, "");
            binder.SubmitChat("hey back");
            Assert.That(sent, Is.EqualTo(new[] { ("hey back", ChatChannel.Whisper, "Bo") }));
            binder.Dispose();
        }
    }
}
