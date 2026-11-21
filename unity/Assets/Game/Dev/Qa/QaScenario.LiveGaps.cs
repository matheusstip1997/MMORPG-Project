#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Gameplay.Dev;
using Game.Gameplay.Entities;
using Game.Gameplay.Streaming;
using Game.UI.Config;
using Game.UI.ViewModels.Hud;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Dev.Qa
{
    /// <summary>
    /// The M4 screens the QA of main after wave 2 could not see live (its section 8): the SO badge of a pending friend request,
    /// the own portfolio tab with a pin and the hide flag, and the stolen mark on the player's own screen.
    /// </summary>
    public sealed partial class QaScenario
    {
        /// <summary>
        /// WP-M4-16 (spec 6.5 R-6.5-04, 9.13): the partner asks the player for friendship (its script <c>qa-partner-friend-request</c>,
        /// <c>friend:partner</c>); the SO button's badge counts the request (shot <c>-badge</c>), the social window opened with its key
        /// lists it (shot <c>-window</c>), and Decline ends it: the request leaves the list and the badge, nothing is left behind.
        /// n/a "partner needed" without <c>-Partner</c>.
        /// </summary>
        private IEnumerator FriendRequest(QaStepResult step)
        {
            var partner = _partner ?? throw new QaNotApplicable(QaPartner.NeededReason);
            var (feed, dev) = PairRig();
            var model = _hud?.SocialModel ?? throw new QaFailure("no social window model on the HUD");
            var window = _hud.SocialWindow ?? throw new QaFailure("no social window on the HUD");
            var link = partner.Link;
            yield return WaitUntil(() => link.PartnerName != null, QaTimeouts.PartnerSeconds, "the partner bot's name on the pair board");
            string partnerName = link.PartnerName!;
            // Review of #279: a request an earlier run left (it failed before its Decline) would hold the badge and could refuse the
            // partner's AddFriend; declined first, each answered before the next.
            var binder = _hud.SocialBinder ?? throw new QaFailure("no social window binder on the HUD");
            foreach (var leftover in model.Incoming.ToList())
            {
                step.log.Add($"declining a request '{leftover.Name}' left by an earlier run");
                binder.Decline(leftover.Ref);
                yield return WaitUntil(() => model.Incoming.All(r => r.Ref != leftover.Ref), QaTimeouts.LootSeconds,
                    $"the leftover request of '{leftover.Name}' declined");
            }
            int request = link.Request("friend-request");
            yield return WaitPartner(partner, request, () => model.Incoming.Any(r => r.Name == partnerName), QaTimeouts.PartnerSeconds,
                "the partner's friend request in the own social state");
            yield return PartnerDone(step, partner, request, "friend-request");
            yield return Settle();
            string badge = _hud.ShortcutRow?.BadgeText("social") ?? "";
            Check(step, "the SO badge counts the waiting request", badge == NumberText.Of(model.IncomingCount) && model.IncomingCount > 0,
                NumberText.Of(model.IncomingCount), badge.Length == 0 ? "no badge" : badge);
            yield return Shot(step, "badge");

            var toggle = Hotkeys.Find(_config, Hotkeys.ToggleSocial) ?? throw new QaFailure("no Hotkeys row toggleSocial");
            yield return Press(_input!.ButtonForPath(toggle.Binding));
            yield return WaitUntil(() => model.Open && window.IsShown, QaTimeouts.UiSeconds, "the social window to open");
            yield return Settle();
            var entry = model.Incoming.First(r => r.Name == partnerName);
            Check(step, "the social window lists the request", window.RowTexts.Any(t => t.Contains(partnerName)), $"a row naming '{partnerName}'",
                string.Join(" | ", window.RowTexts));
            CheckWindowPlace(step, Game.UI.HUD.HudElementIds.SocialWindow);
            yield return Shot(step, "window");

            var decline = window.ListButton("social_decline_" + entry.Ref.ToString(CultureInfo.InvariantCulture))
                          ?? throw new QaFailure("no Decline button on the request row");
            var panel = window.Window.panel ?? throw new QaFailure("the social window is on no panel");
            var mask = new MaskWatch();
            yield return Click(panel, decline, dev, mask);
            yield return WaitUntil(() => model.Incoming.All(r => r.Name != partnerName), QaTimeouts.LootSeconds, "the request to leave the list (declined)");
            yield return Settle();
            Check(step, "Decline ends the request and the badge", model.Incoming.All(r => r.Name != partnerName)
                && (_hud.ShortcutRow?.BadgeText("social") ?? "") == (model.IncomingCount == 0 ? "" : NumberText.Of(model.IncomingCount)),
                "no request from the partner", $"{model.IncomingCount} waiting, badge '{_hud.ShortcutRow?.BadgeText("social")}'");
            Check(step, "the clicks start nothing", mask.Clean, "predicted machine idle", mask.Describe());
            yield return Press(_input.ButtonForPath(toggle.Binding));
            yield return WaitUntil(() => !model.Open, QaTimeouts.UiSeconds, "the social window to close");
            yield return PointerTo(panel, new Vector2(640f, 300f), QaTimeouts.PointerSteps);
            yield return Shot(step);
        }

        /// <summary>
        /// WP-M4-10 part B (spec 5.4 R-5.4-04, M4 decision 11): right after <c>forge-sword</c> (its piece in the Safe bag), the Jobs
        /// window's Portfolio tab shows the own counters (shot <c>-tab</c>); Pin on showcase slot 1 lists the crafted instances of every
        /// container (QA M4 wave 2: it listed the Normal bag only, which holds no instances) and the forged piece pinned is answered
        /// after its commit and shown (shot <c>-pinned</c>); the hide flag set is
        /// answered and shown (shot <c>-hidden</c>), then cleared again so others see the portfolio. n/a below the contracts' level or
        /// without the forged piece.
        /// </summary>
        private IEnumerator Portfolio(QaStepResult step)
        {
            var (feed, dev) = PairRig();
            RequireUnlocked(feed, "contracts");
            var jobsWindow = _hud?.JobsWindow ?? throw new QaFailure("no Jobs window on the HUD");
            var jobsModel = _hud.JobsModel!;
            var portfolio = _hud.PortfolioModel ?? throw new QaFailure("no portfolio model on the HUD");
            var binder = _hud.PortfolioBinder!;
            ushort forged = ItemNetId(Gear.ItemId);
            // forge-sword's piece lands in the Safe bag's equipment area (equip-crafted then wears it).
            var piece = _tap.Inventory!.Slots.Concat((_tap.Inventory.Containers ?? Array.Empty<InventoryContainerEntry>()).SelectMany(c => c.Slots ?? Array.Empty<InventorySlotEntry>()))
                .LastOrDefault(s => s.ItemNetId == forged && s.InstanceId > 0 && s.CrafterId != 0);
            if (piece == null)
                throw new QaNotApplicable($"no forged {Gear.ItemId} held (forge-sword makes one)");
            yield return CloseStation(step); // forge-sword ends at the forge
            var toggle = Hotkeys.Find(_config, Hotkeys.ToggleJobs) ?? throw new QaFailure("no Hotkeys row toggleJobs");
            if (!jobsModel.Open)
                yield return Press(_input!.ButtonForPath(toggle.Binding));
            yield return WaitUntil(() => jobsModel.Open && jobsWindow.IsShown, QaTimeouts.UiSeconds, "the Jobs window to open");
            yield return Settle();
            var panel = jobsWindow.Window.panel ?? throw new QaFailure("the Jobs window is on no panel");
            var mask = new MaskWatch();
            var tab = jobsWindow.Window.Q<Button>("jobs_tab_portfolio") ?? throw new QaFailure("no Portfolio tab in the Jobs window");
            yield return Click(panel, tab, dev, mask);
            yield return WaitUntil(() => jobsWindow.PortfolioShown && portfolio.State != null && portfolio.Pending == 0, QaTimeouts.LootSeconds,
                "the own portfolio in the tab");
            var view = jobsWindow.PortfolioPanel!;
            Check(step, "the Portfolio tab shows the own portfolio", portfolio.State!.Own, "own", string.Join(" | ", view.RowTexts));
            step.log.Add("portfolio: " + string.Join(" | ", view.RowTexts));
            yield return Shot(step, "tab");

            // Slot 1: a pin left by an earlier run is replaced (a pin replaces what the slot showed).
            var pin = view.ButtonNamed("jobs_pin_1") ?? view.ButtonNamed("jobs_unpin_1") ?? throw new QaFailure("no Pin or Unpin on showcase slot 1");
            if (pin.name == "jobs_unpin_1")
            {
                yield return ClickPortfolio(jobsWindow, panel, pin, dev, mask);
                yield return WaitUntil(() => portfolio.Pending == 0 && portfolio.State!.Showcase.All(s => s.ShowcaseSlot != 1), QaTimeouts.LootSeconds,
                    "showcase slot 1 cleared (Unpin)");
                yield return Settle();
                pin = view.ButtonNamed("jobs_pin_1") ?? throw new QaFailure("no Pin on showcase slot 1 after the unpin");
            }
            yield return ClickPortfolio(jobsWindow, panel, pin, dev, mask);
            yield return WaitUntil(() => portfolio.PinTarget == 1, QaTimeouts.UiSeconds, "the pin picker of slot 1");
            yield return Settle();
            int row = binder.PinCandidates.ToList().FindIndex(c => c.InstanceId == piece.InstanceId);
            Check(step, "the picker lists the forged piece", row >= 0, Gear.ItemId + " " + piece.InstanceId, binder.PinCandidates.Count + " candidates");
            Require(step);
            var pick = view.ButtonNamed("jobs_pick_" + row.ToString(CultureInfo.InvariantCulture)) ?? throw new QaFailure("no picker row " + row);
            yield return ClickPortfolio(jobsWindow, panel, pick, dev, mask);
            yield return WaitUntil(() => portfolio.Pending == 0 && portfolio.State!.Showcase.Any(s => s.ShowcaseSlot == 1), QaTimeouts.LootSeconds,
                "the pin answered after its commit");
            var pinned = portfolio.State!.Showcase.First(s => s.ShowcaseSlot == 1);
            Check(step, "showcase slot 1 holds the forged piece", pinned.Item?.ItemNetId == forged, Gear.ItemId,
                pinned.Item == null ? "nothing" : pinned.Item.ItemNetId.ToString(CultureInfo.InvariantCulture));
            yield return Settle();
            yield return Shot(step, "pinned");

            var hide = view.ButtonNamed("jobs_hide") ?? throw new QaFailure("no hide button in the own portfolio");
            bool wasHidden = portfolio.State.Hidden;
            yield return ClickPortfolio(jobsWindow, panel, hide, dev, mask);
            yield return WaitUntil(() => portfolio.Pending == 0 && portfolio.State!.Hidden != wasHidden, QaTimeouts.LootSeconds, "the hide flag answered");
            if (wasHidden)
                step.log.Add("the portfolio was hidden by an earlier run: shown again first");
            if (!portfolio.State!.Hidden)
            {
                yield return Settle();
                hide = view.ButtonNamed("jobs_hide")!;
                yield return ClickPortfolio(jobsWindow, panel, hide, dev, mask);
                yield return WaitUntil(() => portfolio.Pending == 0 && portfolio.State!.Hidden, QaTimeouts.LootSeconds, "the hide flag answered");
            }
            Check(step, "the hide flag is set and shown", portfolio.State!.Hidden, "hidden", "hidden");
            yield return Settle();
            yield return Shot(step, "hidden");
            hide = view.ButtonNamed("jobs_hide")!;
            yield return ClickPortfolio(jobsWindow, panel, hide, dev, mask);
            yield return WaitUntil(() => portfolio.Pending == 0 && !portfolio.State!.Hidden, QaTimeouts.LootSeconds, "the portfolio shown again");
            Check(step, "the hide flag cleared again", !portfolio.State!.Hidden, "shown", "shown");
            Check(step, "the clicks start nothing", mask.Clean, "predicted machine idle", mask.Describe());
            yield return Press(_input!.ButtonForPath(toggle.Binding));
            yield return WaitUntil(() => !jobsModel.Open, QaTimeouts.UiSeconds, "the Jobs window to close");
            yield return PointerTo(panel, new Vector2(640f, 300f), QaTimeouts.PointerSteps);
            yield return Shot(step);
        }

        /// <summary>
        /// WP-M4-9 (R-4.3-03, R-4.4-01, 9.13): the stolen mark on the player's own screen. The partner (its script
        /// <c>qa-partner-take-stolen</c>: <c>meet:stolen-mark</c>, <c>die:dev</c>) meets the player at its spot in <c>m1.combat</c> and dies
        /// there; the player's open inside the owner window is refused <c>BagLocked</c> (shot <c>-locked</c>), it opens it after the window and takes one
        /// row (one no later step spends); the answer says stolen, and the inventory window shows the stack's mark and its tooltip line
        /// (shot <c>-mark</c>). n/a "partner needed" without <c>-Partner</c>.
        /// </summary>
        private IEnumerator TakeStolen(QaStepResult step)
        {
            var partner = _partner ?? throw new QaNotApplicable(QaPartner.NeededReason);
            var (feed, dev) = LootBagRig(step);
            yield return WaitUntil(() => _tap.Inventory != null && !OwnDead(), QaTimeouts.UiSeconds, "the saved bag and the player alive");
            yield return ToDeathDropSpot(step, clearOfBags: true); // where a death drops a bag (R-4.3-03), out of the camps' aggro and clear of other bags
            var link = partner.Link;
            link.TryRead(TakeStolenBagKey, out string earlier);
            int request = link.Request("take-stolen");
            yield return MeetPartner(step, partner, request, "stolen-mark");
            string posted = "";
            yield return WaitPartner(partner, request, () => link.TryRead(TakeStolenBagKey, out posted) && posted != earlier, QaTimeouts.PartnerSeconds,
                "the partner's bag on the pair board (its death)");
            float postedAt = Time.realtimeSinceStartup;
            yield return PartnerDone(step, partner, request, "take-stolen (died, its bag posted)");
            var parts = posted.Split(',');
            if (parts.Length != 3 || !uint.TryParse(parts[0], out uint bagId) || !int.TryParse(parts[1], out int bagX) || !int.TryParse(parts[2], out int bagY))
                throw new QaFailure($"bad bag.at '{posted}'");
            var bag = new OwnLootBagEntry { BagEntityId = bagId, XCm = bagX, YCm = bagY };
            step.log.Add($"the partner's bag {bagId} at ({bagX}, {bagY})");

            var model = _hud!.LootBagModel!;
            var window = _hud.LootBagWindow!;
            // Inside the owner window the server refuses the open (BagLocked, R-4.3-03): a toast, no window.
            var refusals = new List<LootBagResult>();
            void OnRefusal(LootBagResult result) => refusals.Add(result);
            feed.LootBagAnswered += OnRefusal;
            try
            {
                yield return OpenOwnBag(step, feed, bag, waitForWindow: false);
                yield return WaitUntil(() => refusals.Count > 0 || model.Open, QaTimeouts.UiSeconds, "the answer to opening the bag in its owner window");
                Check(step, "inside the owner window the bag is locked to the player", !model.Open && refusals.Any(r => r.Reason == LootBagReason.BagLocked),
                    nameof(LootBagReason.BagLocked), model.Open ? $"the window opened ({model.Content?.Access})" : string.Join(", ", refusals.Select(r => r.Reason)));
                yield return Shot(step, "locked");
            }
            finally
            {
                feed.LootBagAnswered -= OnRefusal;
            }
            if (model.Open)
                model.Close();
            // The owner window of the region's area type (OwnerWindows; m1.combat is openWorld), from when the bag was posted.
            string areaType = QaRegions.AreaFlags(_config, QaRegions.IdOf(ClientWorld.Directory("m1"), _grid.RegionIndexAt(FixVec2.FromCm(bagX, bagY))))?.Id ?? "";
            int ownerWindowMs = Rows<OwnerWindows>().FirstOrDefault(w => w.Id == areaType)?.OwnerWindowMs ?? 0;
            float waitSeconds = Math.Max(0f, ownerWindowMs / 1000f - (Time.realtimeSinceStartup - postedAt)) + QaTimeouts.RespawnMarginSeconds;
            step.log.Add($"waiting out the owner window ({areaType}: {ownerWindowMs} ms, {waitSeconds:0.0} s left)");
            yield return Wait(waitSeconds);

            yield return Press(_input!.ButtonFor("Pickup"));
            yield return WaitUntil(() => model.Open && model.BagEntityId == bagId && model.Content?.Access == LootBagAccess.TakeStolen, QaTimeouts.UiSeconds,
                "the bag window open to take (after the owner window)");
            yield return Settle();
            var rows = model.Content!.Rows;
            if (rows.Length == 0)
                throw new QaFailure($"the partner's bag {bagId} has no rows: its Normal bag held nothing when it died (-PartnerGive gives m1.alienCarapace:2)");
            int index = rows.ToList().FindIndex(r => SpentByNoLaterStep(r.ItemNetId));
            if (index < 0)
                index = 0;
            var chosen = rows[index];
            step.log.Add("rows: " + string.Join(" | ", Enumerable.Range(0, window.RowCount).Select(window.RowLabel)) + $"; taking row {index}");
            var answers = new List<LootBagResult>();
            void OnAnswer(LootBagResult result) => answers.Add(result);
            feed.LootBagAnswered += OnAnswer;
            var panel = window.Window.panel ?? throw new QaFailure("the bag window is on no panel");
            var mask = new MaskWatch();
            try
            {
                yield return Click(panel, window.Row(index), dev, mask);
                yield return WaitUntil(() => answers.Count > 0, QaTimeouts.LootSeconds, "the take's answer (after its commit)");
                var answer = answers[0];
                Check(step, "the take is answered after its commit with the stolen mark", answer.Reason == LootBagReason.None && answer.Stolen
                    && answer.Taken + answer.Mailed == chosen.Count, $"{chosen.Count} taken, stolen", $"{answer.Reason}, {answer.Taken} taken, {answer.Mailed} mailed, stolen {answer.Stolen}");
            }
            finally
            {
                feed.LootBagAnswered -= OnAnswer;
            }
            if (model.Open)
                model.Close();
            yield return WaitUntil(() => _tap.Inventory!.Slots.Any(s => s.ItemNetId == chosen.ItemNetId && (s.Stolen || s.StolenMark != 0)), QaTimeouts.LootSeconds,
                "the stolen stack in the saved bag");
            var stack = _tap.Inventory!.Slots.First(s => s.ItemNetId == chosen.ItemNetId && (s.Stolen || s.StolenMark != 0));

            var inventory = _hud.InventoryWindow!;
            var inventoryModel = _hud.InventoryModel!;
            var toggle = Hotkeys.Find(_config, Hotkeys.ToggleInventory) ?? throw new QaFailure("no Hotkeys row toggleInventory");
            if (!inventoryModel.Open)
                yield return Press(_input.ButtonForPath(toggle.Binding));
            yield return WaitUntil(() => inventoryModel.Open && inventory.IsShown, QaTimeouts.UiSeconds, "the inventory window to open");
            yield return Settle();
            var inventoryPanel = inventory.Window.panel ?? throw new QaFailure("the inventory window is on no panel");
            if (inventoryModel.ActiveContainer != NormalBagCode)
            {
                yield return Click(inventoryPanel, inventory.TabButton(NormalBagCode) ?? throw new QaFailure("no Normal bag tab"), dev, mask);
            }
            var slot = inventory.SlotElement(stack.Slot);
            Check(step, "the inventory slot shows the stolen mark", slot.ClassListContains("inv-slot--stolen"), "inv-slot--stolen", string.Join(" ", slot.GetClasses()));
            yield return PointerTo(inventoryPanel, QaUiPointer.Center(slot), QaTimeouts.PointerSteps);
            yield return Settle();
            string stolenLine = _login.Text.Get(InventoryTextIds.TooltipStolen);
            Check(step, "the tooltip says stolen", inventory.TooltipText.Contains(stolenLine.Split('{')[0].Trim()), stolenLine, inventory.TooltipText.Replace('\n', '|'));
            yield return Shot(step, "mark");
            yield return Press(_input.ButtonForPath(toggle.Binding));
            yield return WaitUntil(() => !inventoryModel.Open, QaTimeouts.UiSeconds, "the inventory window to close");
            yield return PointerTo(inventoryPanel, new Vector2(640f, 300f), QaTimeouts.PointerSteps);
            Check(step, "the clicks start nothing", mask.Clean, "predicted machine idle", mask.Describe());
        }

        /// <summary>Scrolls a button of the Jobs window's portfolio tab into view, then clicks it.</summary>
        private IEnumerator ClickPortfolio(Game.UI.Menus.Jobs.JobsWindowPresenter window, IPanel panel, Button button, PredictionDevClient dev, MaskWatch mask)
        {
            window.PortfolioScroll?.ScrollTo(button);
            yield return Settle();
            yield return Click(panel, button, dev, mask);
        }

        /// <summary>The pair board key the partner's <c>die</c> step posts its bag under (<c>entity,x,y</c>).</summary>
        private const string TakeStolenBagKey = "bag.at";

        /// <summary>
        /// An item no step after <c>take-stolen</c> (nor the crafting and contract steps of a later run) spends: a stolen stack is never
        /// escrowed and never sold at an outpost, and a stolen forge input would taint what it makes.
        /// </summary>
        private bool SpentByNoLaterStep(ushort itemNetId)
        {
            var def = Rows<ItemDef>().FirstOrDefault(d => d.NetId == itemNetId);
            return def != null && def.Id is not ("m3.oreT1" or "m1.cyclopsHide" or "m3.roughGemT1" or "m3.rubyT1" or "m3.enchantStone" or "m2.potionHpSmall"
                or "m1.demonAsh");
        }
    }
}
