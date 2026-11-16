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
    }
}
