#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Game.Gameplay.Dev;
using Game.Gameplay.Entities;
using Game.Gameplay.Streaming;
using Game.UI.Config;
using Game.UI.HUD;
using Game.UI.ViewModels.Hud;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;
using Shared.Simulation.Economy;
using Shared.World;
using UnityEngine.UIElements;

namespace Game.Dev.Qa
{
    public sealed partial class QaScenario
    {
        /// <summary>
        /// WP-M4-10 part B (spec 5.4, M4 decisions 10-12, 14), with the QA partner bot: the partner proposes a sword contract with its
        /// own ore and gold (script <c>contract-sword</c>, row <c>qa-partner-contract-sword</c>) and the player crafts it. The player
        /// walks to the contract board, opens it with the interact key, finds the partner's proposal (its id posted on the pair board)
        /// and clicks Accept; the C badge and the C window (its key) show the contract as accepted; at the forge the banner offers it,
        /// Work on it fixes the sword mold with the customer's slots from the escrow, Start, a hit, the timing bar and Finish make
        /// the sword for the customer (by mail); the player's gold rises by the contract gold less the contract fee and the contract
        /// leaves the list; the partner claims the sword at the mailbox. n/a without the partner, without the Blacksmith job, or below
        /// the contracts' level (the <c>UnlockSchedule</c> row, 20: <c>DbMigrate --set-level</c>).
        /// </summary>
        private IEnumerator ContractSword(QaStepResult step)
        {
            var partner = _partner ?? throw new QaNotApplicable(QaPartner.NeededReason);
            var dev = UnityEngine.Object.FindAnyObjectByType<PredictionDevClient>() ?? throw new QaFailure("no PredictionDevClient in the world scene");
            var feed = dev.Feed ?? throw new QaFailure("no world feed");
            var hud = _hud ?? throw new QaFailure("no HUD");
            var board = hud.ContractBoard ?? throw new QaFailure("no contract board on the HUD");
            var boardModel = hud.ContractBoardModel!;
            var contracts = hud.ContractWindow ?? throw new QaFailure("no C window on the HUD");
            var contractModel = hud.ContractModel!;
            var forgeWindow = hud.ForgeWindow ?? throw new QaFailure("no forge window on the HUD");
            var forgeModel = hud.ForgeModel!;
            yield return WaitUntil(() => feed.JobsState != null && feed.Progression != null && feed.Gold != null && feed.Contracts != null,
                QaTimeouts.UiSeconds, "the own jobs, level, gold and contracts");
            int unlock = Rows<UnlockSchedule>().FirstOrDefault(r => r.Id == "contracts")?.Level ?? 0;
            if (feed.Progression!.Level < unlock)
                throw new QaNotApplicable($"level {feed.Progression.Level}: contracts unlock at {unlock} (DbMigrate --dev --set-level <name> {unlock})");
            var blacksmith = JobDef.Find(_config, "blacksmith") ?? throw new QaFailure("no JobDef blacksmith");
            if (!feed.JobsState!.Jobs.Any(j => j.JobNetId == blacksmith.NetId))
                throw new QaNotApplicable("the character is no Blacksmith (pick-job needs level 12: DbMigrate --set-level)");

            var link = partner.Link;
            int request = link.Request("contract-sword");
            yield return BackToCityOutOfCombat(step);
            var marker = QaMarkers.Load(ClientWorld.Directory("m1")).FirstOrDefault(m => m.id == "m1.npc.contractBoard")
                         ?? throw new QaFailure("no contract board marker m1.npc.contractBoard");
            var boardNpc = NpcDef.Find(_config, "m1.contractBoard") ?? throw new QaFailure("no NpcDef m1.contractBoard");
            yield return WalkTo(FixVec2.FromCm(marker.xCm, marker.yCm - QaTimeouts.NpcStandOffCm), _tap.Hits.Count);
            yield return WaitUntil(() => feed.NpcTarget != 0 && feed.NpcTargetDefId == boardNpc.NetId, QaTimeouts.UiSeconds,
                "the contract board in reach (the NPC prompt)");
            link.PostPosition(_tap.OwnXCm, _tap.OwnYCm);
            int occurrence = link.Arrive("contract");
            yield return WaitUntil(() => link.Check("contract", occurrence) != MeetState.Waiting, QaTimeouts.PartnerSeconds,
                "the partner bot at 'contract'");
            Check(step, "the partner met the player at the board", link.Check("contract", occurrence) == MeetState.Met, "Met",
                link.Check("contract", occurrence).ToString());
            Require(step);
            string posted = "";
            yield return WaitUntil(() => link.TryRead("contract.id", out posted) || partner.EndsWait(request, out _), QaTimeouts.PartnerSeconds,
                "the partner's proposal (its id on the pair board, after its commit)");
            Check(step, "the partner proposed a contract", long.TryParse(posted, out long contractId) && contractId > 0, "a contract id", posted);
            Require(step);

            var interact = Hotkeys.Find(_config, Hotkeys.Interact) ?? throw new QaFailure("no Hotkeys row interact");
            yield return Press(_input!.ButtonForPath(interact.Binding));
            yield return WaitUntil(() => boardModel.Open && board.IsShown && boardModel.HasPage, QaTimeouts.UiSeconds,
                "the contract board window with its first page");
            yield return Settle();
            CheckWindowPlace(step, HudElementIds.ContractBoard);
            var panel = board.Window.panel ?? throw new QaFailure("the contract board is on no panel");
            var mask = new MaskWatch();
            var answers = new List<ContractResult>();
            void OnAnswer(ContractResult result) => answers.Add(result);
            feed.ContractAnswered += OnAnswer;
            try
            {
                int row = -1;
                for (int page = 0; row < 0; page++)
                {
                    row = boardModel.Rows.ToList().FindIndex(r => r.ContractId == contractId);
                    if (row >= 0 || !boardModel.HasMore || page > 10)
                        break;
                    yield return Click(panel, board.MoreButton, dev, mask);
                    yield return WaitUntil(() => boardModel.PendingBoard == 0, QaTimeouts.UiSeconds, "the next page");
                }
                step.log.Add("board: " + string.Join(" | ", board.RowTexts));
                Check(step, "the board lists the partner's proposal", row >= 0, $"contract {contractId}", row >= 0 ? board.RowTexts[row] : "missing");
                Require(step);
                yield return Shot(step, "board");
                yield return Click(panel, board.Row(row), dev, mask);
                yield return WaitUntil(() => boardModel.SelectedRow?.ContractId == contractId, QaTimeouts.UiSeconds, "the row selected");
                yield return Click(panel, board.AcceptButton, dev, mask);
                yield return WaitUntil(() => answers.Count > 0, QaTimeouts.LootSeconds, "the acceptance's answer (after its commit)");
                var accepted = answers[answers.Count - 1];
                Check(step, "the acceptance answered after its commit, nothing refused",
                    accepted.Reason == ContractReason.None && accepted.Action == ContractAction.Accept && accepted.ContractId == contractId,
                    $"accepted {contractId}", $"{accepted.Reason}, {accepted.Action} {accepted.ContractId}");
                Require(step);
                yield return Click(panel, board.Window.Q<Button>("contract_board_close"), dev, mask);
                yield return WaitUntil(() => !boardModel.Open, QaTimeouts.UiSeconds, "the board to close");

                // The C badge and the C window (its key): the contract as the crafter's, accepted.
                yield return WaitUntil(() => contractModel.Entry(contractId)?.Status == ContractStatus.Accepted, QaTimeouts.UiSeconds,
                    "the own contract list with the accepted contract");
                string badge = hud.ShortcutRow?.BadgeText("contracts") ?? "";
                Check(step, "the C badge counts the contract", badge.Length > 0 && badge != "0", "> 0", badge);
                var toggle = Hotkeys.Find(_config, Hotkeys.ToggleContracts) ?? throw new QaFailure("no Hotkeys row toggleContracts");
                yield return Press(_input!.ButtonForPath(toggle.Binding));
                yield return WaitUntil(() => contractModel.Open && contracts.IsShown, QaTimeouts.UiSeconds, "the C window (its key)");
                yield return Settle();
                CheckWindowPlace(step, HudElementIds.ContractWindow);
                Check(step, "the C window lists the accepted contract", contracts.RowTexts.Count > 0
                    && contractModel.Mine.Any(e => e.ContractId == contractId && e.Role == ContractRole.Crafter), "crafting, accepted",
                    string.Join(" | ", contracts.RowTexts));
                yield return Shot(step, "mine");
                yield return Press(_input!.ButtonForPath(toggle.Binding));
                yield return WaitUntil(() => !contractModel.Open, QaTimeouts.UiSeconds, "the C window to close");
            }
            finally
            {
                feed.ContractAnswered -= OnAnswer;
            }

            // The forge: the banner offers the contract; Work on it, Start, a hit, the bar, Finish.
            var forgeMarker = QaMarkers.Load(ClientWorld.Directory("m1")).FirstOrDefault(m => m.id == "m1.npc.forge")
                              ?? throw new QaFailure("no forge marker m1.npc.forge");
            var forgeNpc = NpcDef.Find(_config, "m1.forge") ?? throw new QaFailure("no NpcDef m1.forge");
            yield return WalkTo(FixVec2.FromCm(forgeMarker.xCm, forgeMarker.yCm - QaTimeouts.NpcStandOffCm), _tap.Hits.Count);
            yield return WaitUntil(() => feed.NpcTarget != 0 && feed.NpcTargetDefId == forgeNpc.NetId, QaTimeouts.UiSeconds,
                "the forge in reach (the NPC prompt)");
            yield return Press(_input!.ButtonForPath(interact.Binding));
            yield return WaitUntil(() => forgeModel.Open && forgeWindow.IsShown, QaTimeouts.UiSeconds, "the forge window");
            yield return Settle();
            var forgePanel = forgeWindow.Window.panel ?? throw new QaFailure("the forge window is on no panel");
            var results = new List<ForgeResult>();
            void OnResult(ForgeResult result) => results.Add(result);
            feed.ForgeAnswered += OnResult;
            try
            {
                for (int i = 0; i < forgeModel.ContractOffers.Count && forgeModel.ContractOffers[forgeModel.ContractOffer].ContractId != contractId; i++)
                    yield return Click(forgePanel, forgeWindow.BannerNext, dev, mask);
                Check(step, "the forge banner offers the contract", forgeWindow.BannerShown
                    && forgeModel.ContractOffers.Count > 0 && forgeModel.ContractOffers[forgeModel.ContractOffer].ContractId == contractId,
                    $"contract {contractId}", forgeWindow.BannerText);
                Require(step);
                yield return Shot(step, "banner");
                yield return Click(forgePanel, forgeWindow.BannerWork, dev, mask);
                Check(step, "working on it fixes the sword mold, the customer's slots from the escrow",
                    forgeModel.Contract?.ContractId == contractId && forgeModel.Mold?.Id == "m3.moldSword" && forgeModel.CanStart,
                    "m3.moldSword, ready to start", $"{forgeModel.Mold?.Id}, start {(forgeModel.CanStart ? "on" : "off")}: {forgeWindow.BannerText}");
                Require(step);
                long goldBefore = feed.Gold!.Value;
                long contractGold = forgeModel.Contract!.Gold;
                yield return Click(forgePanel, forgeWindow.StartButton, dev, mask);
                yield return WaitUntil(() => forgeModel.InSession, QaTimeouts.UiSeconds, "the contract's forge session");
                Check(step, "the session runs for the contract", forgeModel.SessionContractId == contractId, NumberText.Of(contractId),
                    NumberText.Of(forgeModel.SessionContractId));
                yield return Click(forgePanel, forgeWindow.ActionButton(1), dev, mask); // hit
                yield return WaitUntil(() => forgeModel.ActionsUsed == 1, QaTimeouts.UiSeconds, "the action in the session");
                yield return PlayForgeBar(step, forgePanel, dev, mask);
                yield return Click(forgePanel, forgeWindow.FinishButton, dev, mask);
                yield return WaitUntil(() => results.Count > 0, QaTimeouts.LootSeconds, "the contract's craft (after its save)");
                var made = results[results.Count - 1];
                Check(step, "the sword is made for the customer", made.Reason == ForgeReason.None && made.OutputItemNetId == ItemNetId("m3.forgedSword"),
                    "m3.forgedSword", $"{made.Reason} item {made.OutputItemNetId}");
                Require(step);
                var rate = Rows<FeeRates>().First(r => r.Id == "contract");
                long fee = FeeMath.Total(FeeType.Contract, contractGold, rate.DefaultPercent, 0);
                yield return WaitUntil(() => feed.Gold == goldBefore + contractGold - fee && feed.Contracts!.Rows.All(r => r.ContractId != contractId),
                    QaTimeouts.LootSeconds, "the payment in the committed gold and the contract gone from the list");
                Check(step, "committed gold: the contract gold less the fee", feed.Gold == goldBefore + contractGold - fee,
                    NumberText.Of(goldBefore + contractGold - fee), NumberText.Of(feed.Gold ?? -1));
                Check(step, "the contract left the own list", feed.Contracts!.Rows.All(r => r.ContractId != contractId), "gone",
                    feed.Contracts.Rows.Any(r => r.ContractId == contractId) ? "still listed" : "gone");
                Check(step, "clicks in the contract windows start nothing", mask.Clean, "predicted machine idle", mask.Describe());
                yield return Shot(step, "done");
                yield return Click(forgePanel, forgeWindow.Window.Q<Button>("forge_close"), dev, mask);
                yield return WaitUntil(() => !forgeModel.Open, QaTimeouts.UiSeconds, "the forge window to close");
            }
            finally
            {
                feed.ForgeAnswered -= OnResult;
            }

            string outcome = "", text = "";
            yield return WaitUntil(() => partner.TryResult(request, out outcome, out text), QaTimeouts.PartnerSeconds,
                "the partner bot's answer to its script 'contract-sword'");
            Check(step, "the partner's script 'contract-sword' done (the sword claimed by mail)", outcome == PairScriptResult.Done,
                PairScriptResult.Done, text);
        }

        /// <summary>
        /// WP-M4-10 (review of #266), with the QA partner bot: the player is the customer. It proposes a sword contract through the C
        /// window's Propose tab (the sword mold, two ore from its bag, 20 gold), posts the id on the pair board, and the partner plays
        /// <c>contract-craft</c> (row <c>qa-partner-contract-craft</c>: takes the Blacksmith job, accepts at the board, forges it, is
        /// paid); the sword comes by mail and the player claims it at the mailbox. Then a second proposal is cancelled from My
        /// contracts (free while open) and its return mail claimed, so the ore stays for <c>forge-sword</c>. n/a without the partner,
        /// below the contracts' level, or with too little ore or gold.
        /// </summary>
        private IEnumerator ContractPropose(QaStepResult step)
        {
            var partner = _partner ?? throw new QaNotApplicable(QaPartner.NeededReason);
            var dev = UnityEngine.Object.FindAnyObjectByType<PredictionDevClient>() ?? throw new QaFailure("no PredictionDevClient in the world scene");
            var feed = dev.Feed ?? throw new QaFailure("no world feed");
            var hud = _hud ?? throw new QaFailure("no HUD");
            var window = hud.ContractWindow ?? throw new QaFailure("no C window on the HUD");
            var model = hud.ContractModel!;
            yield return WaitUntil(() => feed.Progression != null && feed.Gold != null && feed.Contracts != null && feed.MailCount != null
                && _tap.Inventory != null, QaTimeouts.UiSeconds, "the own level, gold, contracts, mail count and saved bag");
            int unlock = Rows<UnlockSchedule>().FirstOrDefault(r => r.Id == "contracts")?.Level ?? 0;
            if (feed.Progression!.Level < unlock)
                throw new QaNotApplicable($"level {feed.Progression.Level}: contracts unlock at {unlock} (DbMigrate --dev --set-level <name> {unlock})");
            ushort ore = ItemNetId("m3.oreT1");
            int OreHeld() => _tap.Inventory!.Slots.Where(s => s.ItemNetId == ore).Sum(s => s.Count);
            const long gold = 20;
            long minGold = Rows<ContractSettings>().First().MinGold;
            if (OreHeld() < 2)
                throw new QaNotApplicable($"{OreHeld()} ore in the Normal bag, 2 needed (-SeedStack seeds m3.oreT1)");
            if (feed.Gold!.Value < gold + minGold)
                throw new QaNotApplicable($"{feed.Gold.Value} gold does not cover {gold + minGold} (the two proposals)");
            yield return BackToCityOutOfCombat(step);

            var link = partner.Link;
            int request = link.Request("contract-craft");
            var answers = new List<ContractResult>();
            void OnAnswer(ContractResult result) => answers.Add(result);
            feed.ContractAnswered += OnAnswer;
            var mask = new MaskWatch();
            long contractId;
            try
            {
                long goldBefore = feed.Gold!.Value;
                int oreBefore = OreHeld();
                yield return ProposeSword(step, dev, window, model, gold, answers, mask, "form");
                var proposed = answers[answers.Count - 1];
                contractId = proposed.ContractId;
                Check(step, "the proposal answered after its commit, nothing refused",
                    proposed.Reason == ContractReason.None && proposed.Action == ContractAction.Propose && contractId > 0 && proposed.Gold == gold,
                    $"proposed for {gold}", $"{proposed.Reason}, {proposed.Action} {contractId} for {proposed.Gold}");
                Require(step);
                yield return WaitUntil(() => feed.Gold == goldBefore - gold && OreHeld() == oreBefore - 2, QaTimeouts.LootSeconds,
                    "the gold and the ore held in escrow (the saved bag and gold)");
                Check(step, "the gold and two ore are held", feed.Gold == goldBefore - gold && OreHeld() == oreBefore - 2,
                    $"{goldBefore - gold} gold, {oreBefore - 2} ore", $"{feed.Gold} gold, {OreHeld()} ore");
                Check(step, "My contracts lists the proposal", model.Mine.Any(e => e.ContractId == contractId && e.Role == ContractRole.Customer),
                    "ordered, on the board", string.Join(" | ", window.RowTexts));
                yield return Shot(step, "proposed");
                yield return CloseContracts(dev, window, model);
            }
            finally
            {
                feed.ContractAnswered -= OnAnswer;
            }

            // The partner accepts and forges it; the sword comes by mail.
            int mailBefore = feed.MailCount!.Open;
            link.Post("contract.id", contractId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            yield return WaitUntil(() => feed.Contracts!.Rows.All(r => r.ContractId != contractId) && feed.MailCount!.Open > mailBefore
                    || partner.EndsWait(request, out _), QaTimeouts.PartnerSeconds * 2,
                "the partner to accept and forge the contract (it leaves the list, its output mail arrives)");
            string outcome = "", text = "";
            yield return WaitUntil(() => partner.TryResult(request, out outcome, out text), QaTimeouts.PartnerSeconds,
                "the partner bot's answer to its script 'contract-craft'");
            Check(step, "the partner's script 'contract-craft' done (accepted, forged, paid)", outcome == PairScriptResult.Done,
                PairScriptResult.Done, text);
            Require(step);
            ushort sword = ItemNetId("m3.forgedSword");
            int SwordsHeld() => _tap.Inventory!.Slots.Concat(_tap.Inventory.Containers.SelectMany(c => c.Slots))
                .Where(s => s.ItemNetId == sword).Sum(s => s.Count);
            int swordsBefore = SwordsHeld();
            var claimed = new MailResult[1];
            yield return ClaimMailOfKind(step, dev, MailKind.ContractOutput, "the sword", claimed);
            Check(step, "the sword claimed from its contract mail", claimed[0]?.Reason == MailReason.None, "None", claimed[0]?.Reason.ToString() ?? "no answer");
            yield return WaitUntil(() => SwordsHeld() == swordsBefore + 1, QaTimeouts.LootSeconds, "the sword in the saved bag");
            Check(step, "saved bag: one forged sword more", SwordsHeld() == swordsBefore + 1, NumberText.Of(swordsBefore + 1), NumberText.Of(SwordsHeld()));
            yield return Shot(step, "claimed");

            // The cancel: a second proposal, cancelled for free while open; its return mail gives the ore and the gold back.
            var cancels = new List<ContractResult>();
            void OnCancel(ContractResult result) => cancels.Add(result);
            feed.ContractAnswered += OnCancel;
            try
            {
                long goldBefore = feed.Gold!.Value;
                int oreBefore = OreHeld();
                yield return ProposeSword(step, dev, window, model, minGold, cancels, mask, "second");
                var second = cancels[cancels.Count - 1];
                Check(step, "the second proposal answered", second.Reason == ContractReason.None && second.ContractId > 0, "None",
                    second.Reason.ToString());
                Require(step);
                long secondId = second.ContractId;
                yield return WaitUntil(() => model.Entry(secondId) != null, QaTimeouts.UiSeconds, "the second proposal in My contracts");
                yield return Settle();
                var panel = window.Window.panel ?? throw new QaFailure("the C window is on no panel");
                var cancel = window.ButtonNamed("contract_cancel_" + secondId) ?? throw new QaFailure("no Cancel on the second proposal");
                Check(step, "an open proposal cancels for free", cancel.text == _login.Text.Get(ContractTextIds.Cancel),
                    _login.Text.Get(ContractTextIds.Cancel), cancel.text);
                int mailBeforeCancel = feed.MailCount!.Open;
                yield return Click(panel, cancel, dev, mask);
                yield return WaitUntil(() => cancels.Count > 1, QaTimeouts.LootSeconds, "the cancel's answer (after its release)");
                var cancelled = cancels[cancels.Count - 1];
                Check(step, "the cancel answered after its release, free", cancelled.Reason == ContractReason.None
                    && cancelled.Action == ContractAction.Cancel && cancelled.Cost == 0, "cancelled, cost 0",
                    $"{cancelled.Reason}, {cancelled.Action}, cost {cancelled.Cost}");
                yield return WaitUntil(() => feed.MailCount!.Open > mailBeforeCancel, QaTimeouts.LootSeconds, "the return mail");
                yield return Shot(step, "cancelled");
                yield return CloseContracts(dev, window, model);
                var returned = new MailResult[1];
                yield return ClaimMailOfKind(step, dev, MailKind.ContractReturn, "the returned ore and gold", returned);
                Check(step, "the return claimed: the ore and the gold back", returned[0]?.Reason == MailReason.None && returned[0]!.GoldTaken == minGold,
                    $"None, {minGold} gold", $"{returned[0]?.Reason}, {returned[0]?.GoldTaken} gold");
                yield return WaitUntil(() => OreHeld() == oreBefore && feed.Gold == goldBefore, QaTimeouts.LootSeconds,
                    "the ore and the gold as before the second proposal");
                Check(step, "the ore and the gold are back", OreHeld() == oreBefore && feed.Gold == goldBefore,
                    $"{oreBefore} ore, {goldBefore} gold", $"{OreHeld()} ore, {feed.Gold} gold");
            }
            finally
            {
                feed.ContractAnswered -= OnCancel;
            }
            Check(step, "clicks in the contract and mail windows start nothing", mask.Clean, "predicted machine idle", mask.Describe());
        }

        /// <summary>
        /// Opens the C window with its key and proposes the sword mold through the Propose tab: two ore from the bag picked for its
        /// two slots, <paramref name="gold"/> typed, Propose clicked; waits for the answer in <paramref name="answers"/>. The window
        /// stays open on My contracts.
        /// </summary>
        private IEnumerator ProposeSword(QaStepResult step, PredictionDevClient dev, Game.UI.Menus.Contracts.ContractWindowPresenter window,
            Game.UI.ViewModels.Contracts.ContractWindowViewModel model, long gold, List<ContractResult> answers, MaskWatch mask, string shot)
        {
            var toggle = Hotkeys.Find(_config, Hotkeys.ToggleContracts) ?? throw new QaFailure("no Hotkeys row toggleContracts");
            if (!model.Open)
                yield return Press(_input!.ButtonForPath(toggle.Binding));
            yield return WaitUntil(() => model.Open && window.IsShown, QaTimeouts.UiSeconds, "the C window (its key)");
            yield return Settle();
            var panel = window.Window.panel ?? throw new QaFailure("the C window is on no panel");
            yield return Click(panel, window.TabButton(Game.UI.ViewModels.Contracts.ContractTab.Propose), dev, mask);
            var form = model.Form;
            for (int i = 0; i < form.Definitions.Count && form.Definition?.Id != "m3.moldSword"; i++)
                yield return Click(panel, window.ButtonNamed("contract_next")!, dev, mask);
            Check(step, "the form steps to the sword mold", form.Definition?.Id == "m3.moldSword", "m3.moldSword", form.Definition?.Id ?? "none");
            Require(step);
            ushort ore = ItemNetId("m3.oreT1");
            for (int input = 0; input < form.Slots.Count; input++)
            {
                yield return Click(panel, window.ButtonNamed("contract_choose_" + input)!, dev, mask);
                yield return Settle();
                int row = form.Candidates(input).FindIndex(e => e.ItemNetId == ore);
                Check(step, $"slot {input + 1}'s picker lists ore", row >= 0, "m3.oreT1", row >= 0 ? "listed" : "missing");
                Require(step);
                yield return Click(panel, window.ButtonNamed("contract_pick_" + row)!, dev, mask);
            }
            window.GoldField.value = gold.ToString(System.Globalization.CultureInfo.InvariantCulture); // the field's own change event
            yield return Settle();
            Check(step, "the form's own checks pass", window.CheckText.Length == 0 && window.ProposeButton.enabledSelf, "no reason, Propose on",
                window.CheckText.Length > 0 ? window.CheckText : "Propose off");
            Require(step);
            yield return Shot(step, shot);
            int before = answers.Count;
            yield return Click(panel, window.ProposeButton, dev, mask);
            yield return WaitUntil(() => answers.Count > before, QaTimeouts.LootSeconds, "the proposal's answer (after its commit)");
            yield return Click(panel, window.TabButton(Game.UI.ViewModels.Contracts.ContractTab.Mine), dev, mask);
        }

        private IEnumerator CloseContracts(PredictionDevClient dev, Game.UI.Menus.Contracts.ContractWindowPresenter window,
            Game.UI.ViewModels.Contracts.ContractWindowViewModel model)
        {
            if (!model.Open)
                yield break;
            var toggle = Hotkeys.Find(_config, Hotkeys.ToggleContracts) ?? throw new QaFailure("no Hotkeys row toggleContracts");
            yield return Press(_input!.ButtonForPath(toggle.Binding));
            yield return WaitUntil(() => !model.Open, QaTimeouts.UiSeconds, "the C window to close");
        }

        /// <summary>
        /// WP-M4-10 part C (spec 5.4; user answers 2026-11-16: one attempt, the stones' supplier in the terms), with the QA partner
        /// bot: the player proposes an enchant contract through the C window's Propose tab (the enchant action, a Blacksmith piece of
        /// its Safe bag's equipment area, its own stones, the minimum gold), posts the id, and the partner plays
        /// <c>contract-enchant</c> (row <c>qa-partner-contract-enchant</c>: takes the Blacksmith job, accepts at the board, enchants
        /// it once at the forge, is paid). The same piece comes back by mail at a level one off at most, and the player claims it at
        /// the mailbox. n/a without the partner, below the contracts' level, without an enchantable piece or stones, or short of gold.
        /// </summary>
        private IEnumerator ContractEnchant(QaStepResult step)
        {
            var partner = _partner ?? throw new QaNotApplicable(QaPartner.NeededReason);
            var dev = UnityEngine.Object.FindAnyObjectByType<PredictionDevClient>() ?? throw new QaFailure("no PredictionDevClient in the world scene");
            var feed = dev.Feed ?? throw new QaFailure("no world feed");
            var hud = _hud ?? throw new QaFailure("no HUD");
            var window = hud.ContractWindow ?? throw new QaFailure("no C window on the HUD");
            var model = hud.ContractModel!;
            yield return WaitUntil(() => feed.Progression != null && feed.Gold != null && feed.Contracts != null && feed.MailCount != null
                && _tap.Inventory != null, QaTimeouts.UiSeconds, "the own level, gold, contracts, mail count and saved bag");
            int unlock = Rows<UnlockSchedule>().FirstOrDefault(r => r.Id == "contracts")?.Level ?? 0;
            if (feed.Progression!.Level < unlock)
                throw new QaNotApplicable($"level {feed.Progression.Level}: contracts unlock at {unlock} (DbMigrate --dev --set-level <name> {unlock})");
            long gold = Rows<ContractSettings>().First().MinGold;
            if (feed.Gold!.Value < gold)
                throw new QaNotApplicable($"{feed.Gold.Value} gold does not cover {gold}");
            var form = model.Form;
            var rules = Shared.Simulation.Crafting.EnchantRules.From(_config) ?? throw new QaFailure("no enchant rules in the tables");
            byte safe = rules.SafeCode;
            InventorySlotEntry[] SafeArea() => _tap.Inventory!.Containers.FirstOrDefault(c => c.Code == safe)?.Slots ?? new InventorySlotEntry[0];
            var piece = SafeArea().FirstOrDefault(e => e.InstanceId != 0 && rules.Item(e.ItemNetId) is { Category: ItemCategory.Equipment } item
                && rules.JobOf(item) == "blacksmith" && e.Enchant < EnchantLimits.MaxLevel && !e.Stolen && e.StolenMark == 0 && !e.Tainted
                && !e.NonTradable && !e.Reserved);
            if (piece == null)
                throw new QaNotApplicable("no enchantable Blacksmith piece in the Safe bag's equipment area (-SeedStack seeds three chest pieces)");
            var pieceDef = rules.Item(piece.ItemNetId)!;
            rules.TryMaterial(pieceDef, out var stone, out int stoneCount);
            int StonesHeld() => _tap.Inventory!.Slots.Where(s => s.ItemNetId == stone.NetId).Sum(s => s.Count);
            if (StonesHeld() < stoneCount)
                throw new QaNotApplicable($"{StonesHeld()} {stone.Id} in the Normal bag, {stoneCount} needed (-SeedStack seeds them)");
            long instance = piece.InstanceId;
            int levelBefore = piece.Enchant;
            step.log.Add($"enchant contract on {pieceDef.Id} {instance} +{levelBefore}, {stoneCount} {stone.Id} from the player");
            yield return BackToCityOutOfCombat(step);

            var link = partner.Link;
            int request = link.Request("contract-enchant");
            var answers = new List<ContractResult>();
            void OnAnswer(ContractResult result) => answers.Add(result);
            feed.ContractAnswered += OnAnswer;
            var mask = new MaskWatch();
            long contractId;
            try
            {
                var toggle = Hotkeys.Find(_config, Hotkeys.ToggleContracts) ?? throw new QaFailure("no Hotkeys row toggleContracts");
                if (!model.Open)
                    yield return Press(_input!.ButtonForPath(toggle.Binding));
                yield return WaitUntil(() => model.Open && window.IsShown, QaTimeouts.UiSeconds, "the C window (its key)");
                yield return Settle();
                var panel = window.Window.panel ?? throw new QaFailure("the C window is on no panel");
                yield return Click(panel, window.TabButton(Game.UI.ViewModels.Contracts.ContractTab.Propose), dev, mask);
                for (int i = 0; i <= form.Definitions.Count && !form.IsEnchant; i++)
                    yield return Click(panel, window.ButtonNamed("contract_next")!, dev, mask);
                Check(step, "the form steps to the enchant action", form.IsEnchant, "Enchant", window.ActionText);
                Require(step);
                // The item: the piece, from the Safe bag's equipment area's picker.
                yield return Click(panel, window.ButtonNamed("contract_choose_0")!, dev, mask);
                yield return Settle();
                int row = form.Candidates(0).FindIndex(e => e.InstanceId == instance);
                Check(step, "the item picker lists the piece", row >= 0, pieceDef.Id, row >= 0 ? "listed" : "missing");
                Require(step);
                yield return Click(panel, window.ButtonNamed("contract_pick_" + row)!, dev, mask);
                // The stones: the player's own (the slot starts as the customer's).
                yield return Click(panel, window.ButtonNamed("contract_choose_1")!, dev, mask);
                yield return Settle();
                int stoneRow = form.Candidates(1).FindIndex(e => e.ItemNetId == stone.NetId);
                Check(step, "the stones picker lists the stones", stoneRow >= 0, stone.Id, stoneRow >= 0 ? "listed" : "missing");
                Require(step);
                yield return Click(panel, window.ButtonNamed("contract_pick_" + stoneRow)!, dev, mask);
                window.GoldField.value = gold.ToString(System.Globalization.CultureInfo.InvariantCulture);
                yield return Settle();
                Check(step, "the enchant form's own checks pass", window.CheckText.Length == 0 && window.ProposeButton.enabledSelf,
                    "no reason, Propose on", window.CheckText.Length > 0 ? window.CheckText : "Propose off");
                Require(step);
                yield return Shot(step, "form");
                long goldBefore = feed.Gold!.Value;
                int stonesBefore = StonesHeld();
                yield return Click(panel, window.ProposeButton, dev, mask);
                yield return WaitUntil(() => answers.Count > 0, QaTimeouts.LootSeconds, "the proposal's answer (after its commit)");
                var proposed = answers[answers.Count - 1];
                contractId = proposed.ContractId;
                Check(step, "the enchant proposal answered after its commit, nothing refused",
                    proposed.Reason == ContractReason.None && proposed.Action == ContractAction.Propose && contractId > 0,
                    "proposed", $"{proposed.Reason}, {proposed.Action} {contractId}");
                Require(step);
                yield return WaitUntil(() => feed.Gold == goldBefore - gold && StonesHeld() == stonesBefore - stoneCount
                    && SafeArea().All(e => e.InstanceId != instance), QaTimeouts.LootSeconds, "the piece, the stones and the gold in escrow");
                Check(step, "the piece, the stones and the gold are held", SafeArea().All(e => e.InstanceId != instance)
                    && StonesHeld() == stonesBefore - stoneCount && feed.Gold == goldBefore - gold, "held",
                    $"piece {(SafeArea().Any(e => e.InstanceId == instance) ? "still in the bag" : "held")}, {StonesHeld()} stones, {feed.Gold} gold");
                yield return Click(panel, window.TabButton(Game.UI.ViewModels.Contracts.ContractTab.Mine), dev, mask);
                yield return Settle();
                Check(step, "My contracts names the enchant", window.RowTexts.Any(t => t.Contains(_login.Text.Get(pieceDef.NameId))),
                    "Enchant " + _login.Text.Get(pieceDef.NameId), string.Join(" | ", window.RowTexts));
                yield return Shot(step, "proposed");
                yield return CloseContracts(dev, window, model);
            }
            finally
            {
                feed.ContractAnswered -= OnAnswer;
            }

            int mailBefore = feed.MailCount!.Open;
            link.Post("contract.id", contractId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            yield return WaitUntil(() => feed.Contracts!.Rows.All(r => r.ContractId != contractId) && feed.MailCount!.Open > mailBefore
                    || partner.EndsWait(request, out _), QaTimeouts.PartnerSeconds * 2,
                "the partner to accept and enchant the piece (the contract leaves the list, its output mail arrives)");
            string outcome = "", text = "";
            yield return WaitUntil(() => partner.TryResult(request, out outcome, out text), QaTimeouts.PartnerSeconds,
                "the partner bot's answer to its script 'contract-enchant'");
            Check(step, "the partner's script 'contract-enchant' done (accepted, enchanted once, paid)", outcome == PairScriptResult.Done,
                PairScriptResult.Done, text);
            Require(step);
            var claimed = new MailResult[1];
            yield return ClaimMailOfKind(step, dev, MailKind.ContractOutput, "the enchanted piece", claimed);
            Check(step, "the piece claimed from its contract mail", claimed[0]?.Reason == MailReason.None, "None", claimed[0]?.Reason.ToString() ?? "no answer");
            yield return WaitUntil(() => SafeArea().Any(e => e.InstanceId == instance), QaTimeouts.LootSeconds, "the piece back in the Safe bag");
            var back = SafeArea().FirstOrDefault(e => e.InstanceId == instance);
            Check(step, "the same piece is back, one attempt off at most", back != null && System.Math.Abs(back.Enchant - levelBefore) <= 1,
                $"instance {instance} +{levelBefore} +/-1", back == null ? "missing" : $"instance {back.InstanceId} +{back.Enchant}");
            yield return Shot(step, "claimed");
            Check(step, "clicks in the contract and mail windows start nothing", mask.Clean, "predicted machine idle", mask.Describe());
        }

        /// <summary>
        /// Walks to the city's mailbox, opens the mail window with the interact key, selects the newest open mail of
        /// <paramref name="kind"/> and claims it; the answer in <paramref name="answer"/>[0]. The window is closed after.
        /// </summary>
        private IEnumerator ClaimMailOfKind(QaStepResult step, PredictionDevClient dev, MailKind kind, string what, MailResult[] answer)
        {
            var feed = dev.Feed!;
            var model = _hud!.MailModel ?? throw new QaFailure("no mail window on the HUD");
            var window = _hud.MailWindow!;
            yield return BackToCityOutOfCombat(step);
            var marker = QaMarkers.Load(ClientWorld.Directory("m1")).FirstOrDefault(m => m.id == "m1.npc.mailbox")
                         ?? throw new QaFailure("no mailbox marker m1.npc.mailbox");
            var mailbox = NpcDef.Find(_config, "m1.mailbox") ?? throw new QaFailure("no NpcDef m1.mailbox");
            yield return WalkTo(FixVec2.FromCm(marker.xCm, marker.yCm - QaTimeouts.NpcStandOffCm), _tap.Hits.Count);
            yield return WaitUntil(() => feed.NpcTarget != 0 && feed.NpcTargetDefId == mailbox.NetId, QaTimeouts.UiSeconds,
                "the mailbox in reach (the NPC prompt)");
            var interact = Hotkeys.Find(_config, Hotkeys.Interact) ?? throw new QaFailure("no Hotkeys row interact");
            yield return Press(_input!.ButtonForPath(interact.Binding));
            yield return WaitUntil(() => model.Open && window.IsShown && model.HasList && model.AtMailbox, QaTimeouts.UiSeconds,
                "the mail window opened by the mailbox, with its list");
            yield return Settle();
            int index = -1;
            for (int i = 0; i < model.Mails.Count; i++)
            {
                if (model.Mails[i].Kind == kind && (index < 0 || model.Mails[i].MailId > model.Mails[index].MailId))
                    index = i;
            }
            Check(step, $"the mailbox lists the {kind} mail with {what}", index >= 0, kind.ToString(), index >= 0 ? "listed" : "missing");
            Require(step);
            long mailId = model.Mails[index].MailId;
            var answers = new List<MailResult>();
            void OnAnswer(MailResult result) => answers.Add(result);
            feed.MailAnswered += OnAnswer;
            var panel = window.Window.panel ?? throw new QaFailure("the mail window is on no panel");
            try
            {
                var mask = new MaskWatch();
                window.ScrollTo(index);
                yield return Settle();
                yield return PointerTo(panel, QaUiPointer.Center(window.Row(index)), QaTimeouts.PointerSteps, dev, mask);
                if (model.Selected == mailId)
                {
                    yield return PointerClick(panel, dev, mask); // a click on the selected row clears it
                    yield return WaitUntil(() => model.Selected == 0, QaTimeouts.UiSeconds, "the selection cleared");
                }
                yield return PointerClick(panel, dev, mask);
                yield return WaitUntil(() => model.Selected == mailId && model.CanClaim, QaTimeouts.UiSeconds, $"the {kind} mail selected");
                yield return PointerTo(panel, QaUiPointer.Center(window.ClaimButton), QaTimeouts.PointerSteps, dev, mask);
                yield return PointerClick(panel, dev, mask);
                yield return WaitUntil(() => answers.Count > 0, QaTimeouts.LootSeconds, "the claim's answer (after its commit)");
                answer[0] = answers[answers.Count - 1];
            }
            finally
            {
                feed.MailAnswered -= OnAnswer;
            }
            var toggle = Hotkeys.Find(_config, Hotkeys.ToggleMail) ?? throw new QaFailure("no Hotkeys row toggleMail");
            yield return Press(_input.ButtonForPath(toggle.Binding));
            yield return WaitUntil(() => !model.Open, QaTimeouts.UiSeconds, "the mail window to close");
            yield return PointerTo(panel, new UnityEngine.Vector2(640f, 300f), QaTimeouts.PointerSteps);
        }
    }
}
