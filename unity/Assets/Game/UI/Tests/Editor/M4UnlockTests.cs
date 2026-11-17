#nullable enable
using System.Collections.Generic;
using System.Linq;
using Game.Gameplay.Entities;
using Game.UI.Config;
using Game.UI.HUD;
using Game.UI.ViewModels.Hud;
using NUnit.Framework;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;

namespace Game.UI.Tests
{
    /// <summary>
    /// The M4 unlock levels (WP-M4-11; M4 decision 14, spec 12.3, user answers 2026-11-17): the committed rows, a level unknown
    /// before the first <c>ProgressionState</c>, the party and trade chat commands and invites below their level, and the toast a
    /// left-edge window asked for during a trade shows. The window binders' own gates are in their window tests.
    /// </summary>
    public sealed class M4UnlockTests
    {
        private static ConfigSet Tables()
        {
            Assert.That(ClientConfig.TryLoad(out var set, out var error), Is.True, error);
            return set;
        }

        private sealed class PartyRequests : IPartyRequests
        {
            public List<(PartyAction Action, uint InviteId, string Name)> Sent { get; } = new();

            public uint RequestParty(PartyAction action, uint inviteId = 0, uint targetEntityId = 0, string targetName = "")
            {
                Sent.Add((action, inviteId, targetName));
                return (uint)Sent.Count;
            }
        }

        private sealed class TradeRequests : ITradeRequests
        {
            public List<TradeAction> Sent { get; } = new();

            public uint RequestTrade(TradeAction action, uint targetEntityId = 0, byte container = 0, ushort slot = 0, ushort count = 0,
                byte offerIndex = 0, long gold = 0)
            {
                Sent.Add(action);
                return (uint)Sent.Count;
            }
        }

        [Test]
        public void Every_m4_feature_waits_for_its_decision_14_level_and_has_its_prompt_and_texts()
        {
            var set = Tables();
            var hud = HudConfig.From(set);
            var view = new HudView(hud);
            Assert.That(new[]
            {
                view.UnlockLevel(HudElementIds.PartyInvite), view.UnlockLevel(HudElementIds.TradeWindow), view.UnlockLevel(HudElementIds.TradeInvite),
                view.UnlockLevel(HudElementIds.ContractWindow), view.UnlockLevel(HudElementIds.AuctionWindow), view.UnlockLevel(HudElementIds.StallWindow),
            }, Is.EqualTo(new[] { 12, 20, 20, 20, 22, 25 }));
            Assert.That(view.UnlockLevel(HudElementIds.PartyFrame), Is.EqualTo(0), "the frame stays: a member is in the party it is in");
            set.TryGet(typeof(UnlockSchedule), out var table);
            var rows = table.Rows.Cast<UnlockSchedule>().ToDictionary(r => r.Id);
            foreach (string id in new[] { "party", "trade", "contracts", "auction", "stalls" })
            {
                Assert.That(rows[id].Trigger, Is.EqualTo(UnlockTrigger.LevelReached), id);
                Assert.That(hud.Text.Has(rows[id].Prompt), Is.True, id + " prompt");
            }
            foreach (string textId in new[] { HudTextIds.PartyLevelLocked, TradeTextIds.LevelLocked, AuctionTextIds.LevelLocked, StallTextIds.LevelLocked })
                Assert.That(hud.Text.Get(textId, new TextArg("level", "12")), Does.Contain("12"), textId);
            Assert.That(hud.Reasons.Has(nameof(ClientReasonCode.WindowBlockedByTrade)), Is.True);
        }

        [Test]
        public void Before_the_first_level_only_what_shows_from_level_1_is_unlocked()
        {
            var view = new HudView(HudConfig.From(Tables()));
            Assert.That(view.Unlocked(HudElementIds.HotbarLeft), Is.True);
            Assert.That(view.Unlocked(HudElementIds.Chat), Is.True); // no row lists it
            Assert.That(view.Unlocked(HudElementIds.PartyInvite), Is.False);
            Assert.That(view.Unlocked(HudElementIds.StallWindow), Is.False);
            view.ApplyLevel(12);
            Assert.That((view.Unlocked(HudElementIds.PartyInvite), view.Unlocked(HudElementIds.TradeWindow)), Is.EqualTo((true, false)));
            view.ApplyLevel(25);
            Assert.That(view.Unlocked(HudElementIds.StallWindow), Is.True);
        }

        [Test]
        public void Below_their_level_the_party_commands_and_trade_toast_the_level_and_send_nothing()
        {
            var set = Tables();
            var config = HudConfig.From(set);
            var models = new HudViewModels(4, 4, config.Chat.HistoryLines, config.Chat.MaxChars);
            var feed = HudChatTests.Feed();
            var party = new PartyRequests();
            var trade = new TradeRequests();
            feed.PartyRequests = party;
            feed.TradeRequests = trade;
            var view = new HudView(config);
            var binder = new HudFeedBinder(config, models, () => 0) { Unlocked = view.Unlocked, UnlockLevel = view.UnlockLevel };
            binder.Bind(feed);
            view.ApplyLevel(11);

            binder.SubmitChat("/invite Bo");
            Assert.That((models.Toast.TextId, models.Toast.ArgValue), Is.EqualTo((HudTextIds.PartyLevelLocked, "12")));
            binder.SubmitChat("/kick Bo");
            binder.SubmitChat("/lead Bo");
            binder.SubmitChat("/trade Bo");
            Assert.That((models.Toast.TextId, models.Toast.ArgValue), Is.EqualTo((TradeTextIds.LevelLocked, "20")));
            binder.SubmitChat("/leave"); // a member below the level may still leave the party it is in
            Assert.That(party.Sent, Is.EqualTo(new[] { (PartyAction.Leave, 0u, "") }));
            Assert.That(trade.Sent, Is.Empty);

            view.ApplyLevel(12);
            binder.SubmitChat("/invite Bo");
            Assert.That(party.Sent.Last(), Is.EqualTo((PartyAction.Invite, 0u, "Bo")));
            view.ApplyLevel(20);
            binder.SubmitChat("/trade Nobody"); // no such player in view: the chat says so, the toast is no level
            Assert.That(models.Chat.Lines.Last().Text, Is.EqualTo(nameof(TradeReason.TradeTargetNotFound)));
            binder.Dispose();
        }

        [Test]
        public void Below_the_party_level_an_invite_is_never_shown_and_is_declined_at_once()
        {
            var feed = HudChatTests.Feed();
            var party = new PartyRequests();
            feed.PartyRequests = party;
            var invite = new PartyInviteViewModel();
            bool unlocked = false;
            var binder = new PartyBinder(new PartyFrameViewModel(), invite, new ToastViewModel(), 1_000, () => 0) { Unlocked = () => unlocked };
            binder.Bind(feed);

            feed.RaisePartyInvite(new PartyInvite { InviteId = 9, InviterName = "Bo", InviterEntityId = 2, TimeoutMs = 3_000 });
            Assert.That(invite.Visible, Is.False);
            Assert.That(party.Sent, Is.EqualTo(new[] { (PartyAction.Decline, 9u, "") }));

            unlocked = true;
            feed.RaisePartyInvite(new PartyInvite { InviteId = 10, InviterName = "Bo", InviterEntityId = 2, TimeoutMs = 3_000 });
            Assert.That(invite.Visible, Is.True);
            Assert.That(party.Sent.Count, Is.EqualTo(1));
            binder.Dispose();
        }
    }
}
