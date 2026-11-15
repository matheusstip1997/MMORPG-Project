#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Gameplay.Dev;
using Game.Gameplay.Entities;
using Game.Gameplay.Streaming;
using Game.UI.HUD;
using Game.UI.ViewModels.Hud;
using Game.UI.ViewModels.Stall;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Dev.Qa
{
    /// <summary>
    /// The party and trade steps with the QA partner bot (WP-M4-2, WP-M4-4; QA M4 wave 1) and what every pair step shares: a
    /// request given up when a step ends before its script answered (F2), and the chat line typed as a player types it.
    /// </summary>
    public sealed partial class QaScenario
    {
        /// <summary>
        /// WP-M4-2 (spec 6.5): the player types <c>/invite &lt;partner name&gt;</c> in chat (the client's <c>ChatCommandParser</c>
        /// path) and the partner plays <c>qa-partner-party-up</c> (<c>party:accept</c>). Passes when the committed party lists both,
        /// the party frame shows the partner's display name, and the script answered done. The party stays for <c>party-chat</c>.
        /// n/a "partner needed" without <c>-Partner</c>.
        /// </summary>
        private IEnumerator PartyUp(QaStepResult step)
        {
            var partner = _partner ?? throw new QaNotApplicable(QaPartner.NeededReason);
            var (feed, _) = PairRig();
            var link = partner.Link;
            yield return WaitUntil(() => link.PartnerName != null, QaTimeouts.PartnerSeconds, "the partner bot's name on the pair board");
            string partnerName = link.PartnerName!;
            int request = link.Request("party-up");
            yield return TypeInChat("/invite " + partnerName);
            yield return WaitPartner(partner, request, () => InPartyWith(feed, partnerName), QaTimeouts.PartnerSeconds, "the party with the partner");
            var members = feed.Party?.State?.Members ?? Array.Empty<PartyMemberState>();
            Check(step, "the party formed with the partner (invited with /invite)", InPartyWith(feed, partnerName),
                $"2 members with '{partnerName}'", string.Join(", ", members.Select(m => m.Name)));
            yield return Settle();
            string[] rows = _hud?.PartyFrame?.ShownRows() ?? Array.Empty<string>();
            Check(step, "the party frame shows the partner's display name", _hud?.PartyFrame?.Visible == true && rows.Any(r => r.StartsWith(partnerName, StringComparison.Ordinal)),
                $"a row '{partnerName} ...'", string.Join(" | ", rows));
            yield return Shot(step);
            yield return PartnerDone(step, partner, request, "party-up");
        }

        /// <summary>
        /// WP-M4-2 (spec 9.12): a party line seen by both. In the partner's party (invited again when <c>party-up</c> left none), the
        /// partner plays <c>qa-partner-party-chat</c> (<c>party:accept</c>, <c>party:chat:hear</c>, <c>party:leave</c>): its party line
        /// reaches the player's Party tab under its display name; the player's <c>/p</c> line echoes on the Party channel and reaches
        /// the partner (its script waits for it); then the partner leaves and the two-member party ends. n/a "partner needed".
        /// </summary>
        private IEnumerator PartyChat(QaStepResult step)
        {
            var partner = _partner ?? throw new QaNotApplicable(QaPartner.NeededReason);
            var (feed, dev) = PairRig();
            var chat = _hud?.Chat ?? throw new QaFailure("no chat on the HUD");
            var model = _hud.Models!.Chat;
            var link = partner.Link;
            yield return WaitUntil(() => link.PartnerName != null, QaTimeouts.PartnerSeconds, "the partner bot's name on the pair board");
            string partnerName = link.PartnerName!;
            uint own = _tap.Registry.OwnEntityId ?? 0;
            int linesBefore = model.Lines.Count;
            int request = link.Request("party-chat");
            if (!InPartyWith(feed, partnerName))
            {
                step.log.Add("not in the partner's party (party-up left none): inviting again");
                yield return TypeInChat("/invite " + partnerName);
            }
            yield return WaitPartner(partner, request, () => InPartyWith(feed, partnerName), QaTimeouts.PartnerSeconds, "the party with the partner");
            yield return WaitPartner(partner, request, () => PartyLineFrom(model, linesBefore, own, partnerName) != null, QaTimeouts.PartnerSeconds,
                "the partner's party line");
            var theirs = PartyLineFrom(model, linesBefore, own, partnerName)!.Value;
            Check(step, "the partner's party line reached the player under its display name", theirs.Name == partnerName && theirs.Channel == ChatChannel.Party,
                $"Party, '{partnerName}'", $"{theirs.Channel}, '{theirs.Name}': {theirs.Text}");

            const string line = "QA smoke: party line";
            int before = model.Lines.Count;
            yield return TypeInChat("/p " + line);
            yield return WaitUntil(() => model.Lines.Skip(before).Any(l => l.SenderEntityId == own && l.Text == line), QaTimeouts.ChatEchoSeconds,
                "the echo of the own party line");
            var echo = model.Lines.Skip(before).First(l => l.SenderEntityId == own && l.Text == line);
            Check(step, "the own /p line echoes on the Party channel", echo.Channel == ChatChannel.Party, "Party", echo.Channel.ToString());

            var panel = chat.TabOf(ChatChannel.Party).panel ?? throw new QaFailure("the chat is on no panel");
            var mask = new MaskWatch();
            yield return Click(panel, chat.TabOf(ChatChannel.Party), dev, mask);
            string[] shown = chat.ShownLines();
            Check(step, "the Party tab shows both lines", shown.Any(s => s.Contains(theirs.Text)) && shown.Any(s => s.Contains(line)),
                $"'{theirs.Text}' and '{line}'", string.Join(" | ", shown));
            yield return Shot(step);
            yield return Click(panel, chat.TabOf(ChatChannel.Local), dev, mask);
            yield return PointerTo(panel, new Vector2(640f, 300f), QaTimeouts.PointerSteps); // off the tabs: the mask is released

            // The partner's party:chat:hear ends on the player's line; then it leaves, which ends the two-member party.
            yield return PartnerDone(step, partner, request, "party-chat (the player's line reached the partner, then it left)");
            yield return WaitUntil(() => feed.Party?.State is not { PartyId: > 0 }, QaTimeouts.UiSeconds, "the party ended by the partner's leave");
            Check(step, "the party ends when the partner leaves", feed.Party?.State is not { PartyId: > 0 }, "no party", "no party");
        }

        /// <summary>
        /// WP-M4-4 (spec 4.5, M4 decision 5) and the left-edge window rule (user decision 2026-11-14: the mail, auction, stall and
        /// loot bag windows stay closed during a trade and none opens). The partner plays <c>qa-partner-trade-item</c>:
        /// <list type="number">
        /// <item>by the auctioneer, the player opens a trade with <c>/trade &lt;name&gt;</c>; while it is open M opens no mail
        /// window and the interact key at the auctioneer no auction window; the player sets <see cref="QaTimeouts.TradeItemGold"/>
        /// gold, the partner offers one demon ash, both lock and confirm: the saved bag holds one ash more and the committed gold
        /// is the price less (the claim-at-mailbox seed mail's gold pays it); after the trade M and the auctioneer open their
        /// windows again;</item>
        /// <item>by a free stall place, a second trade (the partner holds it open): the interact key there opens no stall window;
        /// Cancel ends the trade, and then the interact key opens the stall window.</item>
        /// </list>
        /// The loot bag's pickup key during a trade is checked in <c>die-and-recover</c>, where a bag lies. n/a "partner needed", or
        /// when the player's gold does not cover the price.
        /// </summary>
        private IEnumerator TradeItem(QaStepResult step)
        {
            var partner = _partner ?? throw new QaNotApplicable(QaPartner.NeededReason);
            var (feed, dev) = PairRig();
            var trade = _hud?.TradeModel;
            var window = _hud?.TradeWindow;
            Check(step, "trade window built", trade != null && window != null && feed.Trade != null && _hud!.LeftWindows != null,
                "HudRoot.TradeWindow", window != null ? "built" : "missing");
            Require(step);
            yield return WaitUntil(() => feed.Gold != null && _tap.Inventory != null, QaTimeouts.UiSeconds, "the gold and the saved bag");
            if (feed.Gold!.Value < QaTimeouts.TradeItemGold) // before the partner is asked for anything, as buy-listing
                throw new QaNotApplicable($"{feed.Gold.Value} gold does not cover {QaTimeouts.TradeItemGold} (-SeedStack's seed mail, taken by claim-at-mailbox)");
            var link = partner.Link;
            yield return WaitUntil(() => link.PartnerName != null, QaTimeouts.PartnerSeconds, "the partner bot's name on the pair board");
            string partnerName = link.PartnerName!;
            var interact = Hotkeys.Find(_config, Hotkeys.Interact) ?? throw new QaFailure("no Hotkeys row interact");
            var toggleMail = Hotkeys.Find(_config, Hotkeys.ToggleMail) ?? throw new QaFailure("no Hotkeys row toggleMail");
            var answers = new List<TradeResult>();
            void OnAnswer(TradeResult result) => answers.Add(result);
            feed.TradeAnswered += OnAnswer;
            try
            {
                // 1. By the auctioneer.
                yield return BackToCityOutOfCombat(step);
                var marker = QaMarkers.Load(ClientWorld.Directory("m1")).FirstOrDefault(m => m.id == "m1.npc.auctioneer")
                             ?? throw new QaFailure("no auctioneer marker m1.npc.auctioneer");
                var auctioneer = NpcDef.Find(_config, "m1.auctioneer") ?? throw new QaFailure("no NpcDef m1.auctioneer");
                yield return WalkTo(FixVec2.FromCm(marker.xCm, marker.yCm - QaTimeouts.NpcStandOffCm), _tap.Hits.Count);
                yield return WaitUntil(() => feed.NpcTarget != 0 && feed.NpcTargetDefId == auctioneer.NetId, QaTimeouts.UiSeconds,
                    "the auctioneer in reach (the NPC prompt)");
                int request = link.Request("trade-item");
                yield return MeetPartner(step, partner, request, "trade-auction");
                yield return OpenTrade(step, partner, request, feed, partnerName);
                yield return Press(_input!.ButtonForPath(toggleMail.Binding));
                yield return Wait(QaTimeouts.TradeBlockedSeconds);
                Check(step, "during the trade M opens no mail window", !_hud!.MailModel!.Open, "closed", _hud.MailModel.Open ? "open" : "closed");
                yield return Press(_input.ButtonForPath(interact.Binding));
                yield return Wait(QaTimeouts.TradeBlockedSeconds);
                Check(step, "during the trade the auctioneer opens no auction window", !_hud.AuctionModel!.Open,
                    "closed", _hud.AuctionModel.Open ? "open" : "closed");
                Check(step, "during the trade no left-edge window is open", _hud.LeftWindows!.OpenId() == null && _hud.LeftWindows.IsBlocked,
                    "none, blocked", $"{_hud.LeftWindows.OpenId() ?? "none"}, blocked {_hud.LeftWindows.IsBlocked}");
                Check(step, "the trade stays open", trade!.Open && window!.IsShown, "open", trade.Open ? "open" : "closed");
                yield return Shot(step, "blocked");

                ushort ash = ItemNetId("m1.demonAsh");
                int ashBefore = _tap.Inventory!.Slots.Where(s => s.ItemNetId == ash).Sum(s => s.Count);
                long goldBefore = feed.Gold!.Value;
                var panel = window!.Panel.panel ?? throw new QaFailure("the trade window is on no panel");
                var mask = new MaskWatch();
                window.GoldField.value = QaTimeouts.TradeItemGold.ToString(CultureInfo.InvariantCulture);
                var setGold = window.Panel.Q<Button>("trade_set_gold") ?? throw new QaFailure("no trade_set_gold button");
                yield return Click(panel, setGold, dev, mask);
                yield return WaitPartner(partner, request, () => feed.Trade!.State is { } s && s.OwnGold == QaTimeouts.TradeItemGold
                    && ClientTrade.Units(s.PartnerOffer, ash) >= 1, QaTimeouts.PartnerSeconds, "the own gold set and the partner's ash offered");
                Check(step, "the window shows the partner's offer", window.PartnerRow(0).text.Length > 0, "a row", window.PartnerRow(0).text);
                yield return WaitUntil(() => trade.CanLock, QaTimeouts.UiSeconds, "Lock enabled");
                yield return Click(panel, window.LockButton, dev, mask);
                yield return WaitPartner(partner, request, () => trade.CanConfirm, QaTimeouts.PartnerSeconds, "both sides locked (Confirm enabled)");
                yield return Shot(step, "locked");
                yield return Click(panel, window.ConfirmButton, dev, mask);
                yield return WaitPartner(partner, request, () => feed.Trade!.State is { Phase: TradePhase.Done }, QaTimeouts.PartnerSeconds,
                    "the trade done (after both saves committed)");
                Check(step, "the confirm answered, nothing refused", answers.All(a => a.Reason == TradeReason.None), "no refusal",
                    string.Join(", ", answers.Where(a => a.Reason != TradeReason.None).Select(a => $"{a.Action} {a.Reason}")));
                yield return WaitUntil(() => _tap.Inventory!.Slots.Where(s => s.ItemNetId == ash).Sum(s => s.Count) == ashBefore + 1
                    && feed.Gold == goldBefore - QaTimeouts.TradeItemGold, QaTimeouts.LootSeconds, "the saved bag and the gold after the trade");
                Check(step, "saved bag: 1 demon ash more", _tap.Inventory!.Slots.Where(s => s.ItemNetId == ash).Sum(s => s.Count) == ashBefore + 1,
                    NumberText.Of(ashBefore + 1), NumberText.Of(_tap.Inventory.Slots.Where(s => s.ItemNetId == ash).Sum(s => s.Count)));
                Check(step, $"committed gold: {QaTimeouts.TradeItemGold} less", feed.Gold == goldBefore - QaTimeouts.TradeItemGold,
                    NumberText.Of(goldBefore - QaTimeouts.TradeItemGold), NumberText.Of(feed.Gold ?? -1));
                Check(step, "clicks in the trade window start nothing", mask.Clean, "predicted machine idle", mask.Describe());
                yield return WaitUntil(() => !trade.Open, QaTimeouts.UiSeconds, "the trade window to close");
                yield return RestoreInventoryAfterTrade(step);
                yield return PointerTo(panel, new Vector2(640f, 300f), QaTimeouts.PointerSteps);

                // After the trade the windows open again.
                yield return Press(_input.ButtonForPath(toggleMail.Binding));
                yield return WaitUntil(() => _hud.MailModel.Open, QaTimeouts.UiSeconds, "the mail window with M after the trade");
                Check(step, "after the trade M opens the mail window", _hud.MailModel.Open, "open", "open");
                yield return Press(_input.ButtonForPath(toggleMail.Binding));
                yield return WaitUntil(() => !_hud.MailModel.Open, QaTimeouts.UiSeconds, "the mail window to close");
                yield return Press(_input.ButtonForPath(interact.Binding));
                yield return WaitUntil(() => _hud.AuctionModel.Open, QaTimeouts.UiSeconds, "the auction window from the auctioneer after the trade");
                Check(step, "after the trade the auctioneer opens the auction window", _hud.AuctionModel.Open, "open", "open");
                var auctionPanel = _hud.AuctionWindow!.Window.panel ?? throw new QaFailure("the auction window is on no panel");
                yield return CloseAuction(step, auctionPanel, dev, mask);

                // 2. By a free stall place: a trade held open by the partner, then cancelled by the player.
                var stalls = feed.Stalls ?? throw new QaFailure("no stall client");
                int place = -1;
                for (int i = 0; i < stalls.Places.Count && place < 0; i++)
                {
                    if (ClientStalls.StallOn(_tap.Registry, i) == 0)
                        place = i;
                }
                if (place < 0)
                    throw new QaFailure("no free stall place in view");
                var at = stalls.Places[place];
                yield return WalkTo(FixVec2.FromCm(at.XCm, at.YCm + QaTimeouts.StallStandOffCm), _tap.Hits.Count);
                yield return WaitUntil(() => feed.StallTarget == 0 && feed.StallPlaceTarget == place, QaTimeouts.UiSeconds,
                    "the free stall place in reach (the stall prompt)");
                yield return MeetPartner(step, partner, request, "trade-stall");
                yield return OpenTrade(step, partner, request, feed, partnerName);
                var stall = _hud.StallModel!;
                yield return Press(_input.ButtonForPath(interact.Binding));
                yield return Wait(QaTimeouts.TradeBlockedSeconds);
                Check(step, "during the trade the stall place opens no stall window", !stall.Open, "closed", stall.Open ? "open" : "closed");
                yield return Shot(step, "stall-blocked");
                var tradePanel = window.Panel.panel ?? throw new QaFailure("the trade window is on no panel");
                yield return Click(tradePanel, window.CancelButton, dev, mask);
                yield return WaitUntil(() => !trade.Open && feed.Trade!.State is not { Phase: TradePhase.Open or TradePhase.Settling },
                    QaTimeouts.UiSeconds, "the trade cancelled");
                yield return RestoreInventoryAfterTrade(step);
                yield return PointerTo(tradePanel, new Vector2(640f, 300f), QaTimeouts.PointerSteps);
                yield return Press(_input.ButtonForPath(interact.Binding));
                yield return WaitUntil(() => stall.Open && _hud.StallWindow!.IsShown && stall.Mode == StallWindowMode.Open, QaTimeouts.UiSeconds,
                    "the stall window after the trade");
                Check(step, "after the trade the stall place opens the stall window", stall.Open, "open", "open");
                yield return Shot(step, "stall-after");
                var stallPanel = _hud.StallWindow!.Window.panel ?? throw new QaFailure("the stall window is on no panel");
                var close = _hud.StallWindow.Window.Q<Button>("stall_close") ?? throw new QaFailure("no stall_close button");
                yield return Click(stallPanel, close, dev, mask);
                yield return WaitUntil(() => !stall.Open, QaTimeouts.UiSeconds, "the stall window to close");
                yield return PointerTo(stallPanel, new Vector2(640f, 300f), QaTimeouts.PointerSteps);
                Check(step, "clicks in the windows start nothing", mask.Clean, "predicted machine idle", mask.Describe());
                yield return PartnerDone(step, partner, request, "trade-item (one ash for the gold, then a held trade cancelled)");
            }
            finally
            {
                feed.TradeAnswered -= OnAnswer;
            }
            yield return Settle();
        }

        /// <summary>
        /// die-and-recover with the partner (QA M4 wave 1, user decision 2026-11-14): at the own bag, with its prompt shown, the partner
        /// (<c>qa-partner-trade-hold</c>) meets the player and holds a trade open; the pickup key there sends nothing and opens no bag
        /// window (the client's "a trade is open" line); Cancel ends the trade. The caller then presses the pickup key again.
        /// </summary>
        private IEnumerator PickupBlockedDuringTrade(QaStepResult step, WorldClientFeed feed, PredictionDevClient dev)
        {
            if (_partner is not { } partner)
            {
                step.log.Add("the pickup key during a trade: n/a without -Partner");
                yield break;
            }
            var link = partner.Link;
            yield return WaitUntil(() => link.PartnerName != null, QaTimeouts.PartnerSeconds, "the partner bot's name on the pair board");
            string partnerName = link.PartnerName!;
            int request = link.Request("trade-hold");
            yield return MeetPartner(step, partner, request, "bag-trade");
            yield return OpenTrade(step, partner, request, feed, partnerName);
            var model = _hud!.LootBagModel!;
            int logged = step.log.Count;
            yield return Press(_input!.ButtonFor("Pickup"));
            yield return Wait(QaTimeouts.TradeBlockedSeconds);
            const string blocked = "a trade is open: the loot bag window stays closed";
            Check(step, "during a trade the pickup key at the bag asks for nothing", step.log.Skip(logged).Any(l => l.Contains(blocked)),
                "\"" + blocked + "\"", string.Join(" | ", step.log.Skip(logged)));
            Check(step, "during a trade no bag window opens", !model.Open, "closed", model.Open ? "open" : "closed");
            yield return Shot(step, "trade-blocked");
            var window = _hud.TradeWindow!;
            var panel = window.Panel.panel ?? throw new QaFailure("the trade window is on no panel");
            var mask = new MaskWatch();
            yield return Click(panel, window.CancelButton, dev, mask);
            yield return WaitUntil(() => !_hud.TradeModel!.Open, QaTimeouts.UiSeconds, "the trade cancelled");
            yield return RestoreInventoryAfterTrade(step);
            yield return PointerTo(panel, new Vector2(640f, 300f), QaTimeouts.PointerSteps);
            yield return PartnerDone(step, partner, request, "trade-hold");
        }

        /// <summary>The world feed and the dev client, checked once per pair step.</summary>
        private (WorldClientFeed feed, PredictionDevClient dev) PairRig()
        {
            var dev = UnityEngine.Object.FindAnyObjectByType<PredictionDevClient>() ?? throw new QaFailure("no PredictionDevClient in the world scene");
            return (dev.Feed ?? throw new QaFailure("no world feed"), dev);
        }

        private static bool InPartyWith(WorldClientFeed feed, string name) =>
            feed.Party?.State is { PartyId: > 0 } state && state.Members.Length == 2 && state.Members.Any(m => m.Name == name);

        /// <summary>The first party line after <paramref name="from"/> from someone else under <paramref name="name"/>.</summary>
        private static ChatLine? PartyLineFrom(ChatViewModel model, int from, uint own, string name)
        {
            for (int i = from; i < model.Lines.Count; i++)
            {
                var line = model.Lines[i];
                if (line.Kind == ChatLineKind.Player && line.Channel == ChatChannel.Party && line.SenderEntityId != own && line.Name == name)
                    return line;
            }
            return null;
        }

        /// <summary>Posts the player's position, arrives at <paramref name="point"/> and waits for the partner there.</summary>
        private IEnumerator MeetPartner(QaStepResult step, QaPartner partner, int request, string point)
        {
            var link = partner.Link;
            link.PostPosition(_tap.OwnXCm, _tap.OwnYCm);
            int occurrence = link.Arrive(point);
            yield return WaitPartner(partner, request, () => link.Check(point, occurrence) != MeetState.Waiting, QaTimeouts.PartnerSeconds,
                $"the partner bot at the meeting point '{point}'");
            var met = link.Check(point, occurrence);
            Check(step, $"the partner met the player at '{point}'", met == MeetState.Met, "Met", met.ToString());
            Require(step);
        }

        /// <summary><c>/trade &lt;name&gt;</c> typed in chat; the partner accepts; the trade window opens.</summary>
        private IEnumerator OpenTrade(QaStepResult step, QaPartner partner, int request, WorldClientFeed feed, string partnerName)
        {
            _inventoryOpenBeforeTrade = _hud!.InventoryModel?.Open ?? false;
            yield return TypeInChat("/trade " + partnerName);
            yield return WaitPartner(partner, request, () => _hud!.TradeModel!.Open && _hud.TradeWindow!.IsShown && feed.Trade!.IsOpen,
                QaTimeouts.PartnerSeconds, "the trade window with the partner (invited with /trade)");
            Check(step, "/trade opens the trade window with the partner", feed.Trade!.State?.PartnerName == partnerName, partnerName,
                feed.Trade.State?.PartnerName ?? "none");
            yield return Settle();
        }

        /// <summary>Whether the inventory window was open before <see cref="OpenTrade"/> (an opening trade opens it beside the window).</summary>
        private bool _inventoryOpenBeforeTrade;

        /// <summary>
        /// A trade that opened opens the inventory window too (<c>TradeBinder</c>, to drag from it); once the trade ended the step
        /// leaves the window as it found it: closed, or <c>move-stack</c>'s key closed it instead of opening it (smoke warrior B).
        /// </summary>
        private IEnumerator RestoreInventoryAfterTrade(QaStepResult step)
        {
            var inventory = _hud!.InventoryModel;
            if (_inventoryOpenBeforeTrade || inventory is not { Open: true })
                yield break;
            step.log.Add("the trade opened the inventory window: closed again");
            inventory.SetOpen(false);
            yield return WaitUntil(() => !inventory.Open, QaTimeouts.UiSeconds, "the inventory window to close after the trade");
        }

        /// <summary>Waits for the partner's answer to <paramref name="request"/> and checks it is done.</summary>
        private IEnumerator PartnerDone(QaStepResult step, QaPartner partner, int request, string what)
        {
            string outcome = "", text = "";
            yield return WaitUntil(() => partner.TryResult(request, out outcome, out text), QaTimeouts.PartnerSeconds,
                $"the partner bot's answer to its script {what}");
            Check(step, $"the partner's script {what} done", outcome == PairScriptResult.Done, PairScriptResult.Done, text);
        }

        /// <summary>
        /// <see cref="WaitUntil"/> that fails at once with the partner's answer when its script ends before the condition holds
        /// (QA M4 wave 1 F2: a failed bot entry no longer leaves the smoke waiting out a timeout for something that cannot come).
        /// </summary>
        private IEnumerator WaitPartner(QaPartner partner, int request, Func<bool> condition, float seconds, string what)
        {
            string outcome = "", text = "";
            yield return WaitUntil(() => condition() || partner.TryResult(request, out outcome, out text), seconds, what);
            if (!condition())
                throw new QaFailure($"the partner's script answered before {what}: {text}");
        }

        /// <summary>
        /// QA M4 wave 1 F2: after every step, each partner request still without an answer (the step failed, timed out or threw
        /// before it) is given up (<c>cancel.&lt;n&gt;</c>); the bot stops that script and answers "cancelled", so the next pair step
        /// is judged on its own. Waits up to <see cref="QaTimeouts.PartnerCancelSeconds"/> for those answers.
        /// </summary>
        private IEnumerator FreePartner(string stepName)
        {
            if (_partner == null)
                yield break;
            var link = _partner.Link;
            int from = 1, to = link.Requests;
            if (link.CancelUnanswered() == 0)
                yield break;
            Debug.Log($"[QA] {stepName} ended before its partner script answered: the request is given up (cancel)");
            float started = Time.realtimeSinceStartup;
            bool Answered()
            {
                for (int n = from; n <= to; n++)
                {
                    if (!link.TryReadResult(n, out _))
                        return false;
                }
                return true;
            }
            while (!Answered() && Time.realtimeSinceStartup - started < QaTimeouts.PartnerCancelSeconds)
                yield return null;
            for (int n = from; n <= to; n++)
                Debug.Log($"[QA] partner request {n}: " + (link.TryReadResult(n, out string result) ? result : "no answer"));
        }

        /// <summary>After a failed step: out of a party and a trade the step left behind (QA M4 wave 1 F2).</summary>
        private void LeavePartyAndTrade()
        {
            var feed = UnityEngine.Object.FindAnyObjectByType<PredictionDevClient>()?.Feed;
            if (feed == null)
                return;
            if (feed.Trade?.IsOpen == true)
                feed.TradeRequests?.RequestTrade(TradeAction.Cancel);
            else if (feed.Trade?.IsInvited == true)
                feed.TradeRequests?.RequestTrade(TradeAction.Decline);
            if (feed.Party?.State is { PartyId: > 0 })
                feed.PartyRequests?.RequestParty(PartyAction.Leave);
        }

        /// <summary>Types a chat line as a player does: Enter opens the input, the line, Enter sends it (see <c>ChatEcho</c>).</summary>
        private IEnumerator TypeInChat(string text)
        {
            var model = _hud!.Models!.Chat;
            var field = _hud.GetComponent<UIDocument>().rootVisualElement.Q<TextField>("chat_input")
                        ?? throw new QaFailure("no chat_input field in the HUD");
            yield return Press(_input!.Keyboard.enterKey);
            yield return WaitUntil(() => model.InputOpen, QaTimeouts.UiSeconds, "the chat input to open");
            QaUiKeys.Press(field, KeyCode.Return, '\n'); // the press that opened the input reaches the field too (see ChatEcho)
            yield return null;
            if (!QaUiKeys.Type(field, text))
                throw new QaFailure("the chat input has no focus for " + text);
            yield return null;
            yield return Press(_input.Keyboard.enterKey);
            yield return WaitUntil(() => !model.InputOpen, QaTimeouts.UiSeconds, "the chat input to close after " + text);
        }
    }
}
