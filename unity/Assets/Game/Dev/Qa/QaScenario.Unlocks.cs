#nullable enable
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Game.Gameplay.Entities;
using Game.Gameplay.Streaming;
using Game.UI.Config;
using Game.UI.ViewModels.Hud;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;

namespace Game.Dev.Qa
{
    /// <summary>
    /// The M4 unlock levels (WP-M4-11; M4 decision 14, spec 12.3; client only, the server takes every request at any level): the
    /// pair steps go n/a below their feature's level before the partner is asked, and <c>unlock-levels</c> checks what each entry
    /// point does at the character's level.
    /// </summary>
    public sealed partial class QaScenario
    {
        /// <summary>The level of the <c>UnlockSchedule</c> row <paramref name="rowId"/>; 0 without the row.</summary>
        private int UnlockLevelOf(string rowId) => Rows<UnlockSchedule>().FirstOrDefault(r => r.Id == rowId)?.Level ?? 0;

        /// <summary>
        /// n/a below the highest level of <paramref name="rowIds"/> (thrown before the step asks its partner anything, so no partner
        /// script starts); <c>unlock-levels</c> checks the locked paths at that level instead.
        /// </summary>
        private void RequireUnlocked(WorldClientFeed feed, params string[] rowIds)
        {
            int level = feed.Progression?.Level ?? 0;
            foreach (string rowId in rowIds)
            {
                int unlock = UnlockLevelOf(rowId);
                if (level < unlock)
                    throw new QaNotApplicable($"level {level}: the {rowId} unlock level is {unlock} (Run-QaSmoke.ps1 -SeedLevel {unlock}; " +
                                              "unlock-levels checks the locked paths)");
            }
        }

        /// <summary>
        /// WP-M4-11 (M4 decision 14, spec 12.3): at the character's level, each M4 entry point the smoke can reach without a partner.
        /// <list type="bullet">
        /// <item>Party (row <c>party</c>): <c>/invite</c> of a name nobody has; below the level the toast says the level and no request
        /// goes out (no answer), from it the server answers the request.</item>
        /// <item>Trade (row <c>trade</c>): <c>/trade</c> of a name nobody has; below the level the toast says the level, from it the
        /// chat says no such player is in view.</item>
        /// <item>Contracts (row <c>contracts</c>): the C key; below the level the toast says the level and the window stays closed,
        /// from it the window opens (and closes again).</item>
        /// <item>Below the auction's level (row <c>auction</c>): the interaction key at the auctioneer toasts the level and opens nothing.
        /// From it <c>list-item</c> opens the window.</item>
        /// <item>Below the stalls' level (row <c>stalls</c>): at a free stall place no stall prompt shows and the interaction key toasts
        /// the level. From it <c>open-stall</c> opens one.</item>
        /// </list>
        /// </summary>
        private IEnumerator UnlockLevels(QaStepResult step)
        {
            var (feed, _) = PairRig();
            var hud = _hud ?? throw new QaFailure("no HUD");
            var toast = hud.Models!.Toast;
            var chat = hud.Models.Chat;
            yield return WaitUntil(() => feed.Progression != null, QaTimeouts.UiSeconds, "the own level");
            int level = feed.Progression!.Level;
            step.log.Add($"level {level}; party {UnlockLevelOf("party")}, trade {UnlockLevelOf("trade")}, contracts {UnlockLevelOf("contracts")}, " +
                         $"auction {UnlockLevelOf("auction")}, stalls {UnlockLevelOf("stalls")}");
            const string nobody = "QaNobodyHere";

            // Party.
            int partyLevel = UnlockLevelOf("party");
            var partyAnswers = new List<PartyResult>();
            void OnParty(PartyResult result) => partyAnswers.Add(result);
            feed.PartyAnswered += OnParty;
            try
            {
                yield return TypeInChat("/invite " + nobody);
                if (level < partyLevel)
                {
                    yield return Wait(QaTimeouts.TradeBlockedSeconds);
                    Check(step, "below the party level /invite toasts the level and sends nothing",
                        toast.TextId == HudTextIds.PartyLevelLocked && toast.ArgValue == NumberText.Of(partyLevel) && partyAnswers.Count == 0,
                        $"{HudTextIds.PartyLevelLocked} {partyLevel}, no answer", $"'{toast.TextId}' {toast.ArgValue}, {partyAnswers.Count} answer(s)");
                }
                else
                {
                    yield return WaitUntil(() => partyAnswers.Count > 0, QaTimeouts.UiSeconds, "the server's answer to /invite");
                    Check(step, "from the party level /invite goes to the server", partyAnswers.Count > 0 && toast.TextId != HudTextIds.PartyLevelLocked,
                        "an answer", $"{partyAnswers.Count} answer(s), {partyAnswers.LastOrDefault()?.Reason}");
                }
            }
            finally
            {
                feed.PartyAnswered -= OnParty;
            }
            yield return Shot(step, "party");

            // Trade.
            int tradeLevel = UnlockLevelOf("trade");
            int linesBefore = chat.Lines.Count;
            yield return TypeInChat("/trade " + nobody);
            yield return Wait(QaTimeouts.TradeBlockedSeconds);
            bool notFound = chat.Lines.Skip(linesBefore).Any(l => l.Kind == ChatLineKind.Refusal && l.Text == nameof(TradeReason.TradeTargetNotFound));
            if (level < tradeLevel)
                Check(step, "below the trade level /trade toasts the level", toast.TextId == TradeTextIds.LevelLocked && !notFound,
                    $"{TradeTextIds.LevelLocked} {tradeLevel}", $"'{toast.TextId}' {toast.ArgValue}, not-found line {notFound}");
            else
                Check(step, "from the trade level /trade looks for the player", notFound && toast.TextId != TradeTextIds.LevelLocked,
                    nameof(TradeReason.TradeTargetNotFound), notFound ? "not found" : $"'{toast.TextId}'");

            // Contracts: the C key.
            int contractLevel = UnlockLevelOf("contracts");
            var toggleContracts = Hotkeys.Find(_config, Hotkeys.ToggleContracts) ?? throw new QaFailure("no Hotkeys row toggleContracts");
            var contractModel = hud.ContractModel ?? throw new QaFailure("no C window model on the HUD");
            yield return Press(_input!.ButtonForPath(toggleContracts.Binding));
            yield return Wait(QaTimeouts.TradeBlockedSeconds);
            if (level < contractLevel)
                Check(step, "below the contracts level the C key toasts the level and opens nothing",
                    !contractModel.Open && toast.TextId == ContractTextIds.Locked, $"closed, {ContractTextIds.Locked}",
                    $"{(contractModel.Open ? "open" : "closed")}, '{toast.TextId}'");
            else
            {
                Check(step, "from the contracts level the C key opens the C window", contractModel.Open, "open", contractModel.Open ? "open" : "closed");
                yield return Press(_input.ButtonForPath(toggleContracts.Binding));
                yield return WaitUntil(() => !contractModel.Open, QaTimeouts.UiSeconds, "the C window to close");
            }

            var interact = Hotkeys.Find(_config, Hotkeys.Interact) ?? throw new QaFailure("no Hotkeys row interact");
            int auctionLevel = UnlockLevelOf("auction");
            if (level < auctionLevel)
            {
                yield return BackToCityOutOfCombat(step);
                var marker = QaMarkers.Load(ClientWorld.Directory("m1")).FirstOrDefault(m => m.id == "m1.npc.auctioneer")
                             ?? throw new QaFailure("no auctioneer marker m1.npc.auctioneer");
                var auctioneer = NpcDef.Find(_config, "m1.auctioneer") ?? throw new QaFailure("no NpcDef m1.auctioneer");
                yield return WalkTo(FixVec2.FromCm(marker.xCm, marker.yCm - QaTimeouts.NpcStandOffCm), _tap.Hits.Count);
                yield return WaitUntil(() => feed.NpcTarget != 0 && feed.NpcTargetDefId == auctioneer.NetId, QaTimeouts.UiSeconds,
                    "the auctioneer in reach (the NPC prompt)");
                yield return Press(_input.ButtonForPath(interact.Binding));
                yield return WaitUntil(() => toast.TextId == AuctionTextIds.LevelLocked, QaTimeouts.UiSeconds, "the auction's level toast");
                Check(step, "below the auction level the auctioneer toasts the level and opens nothing",
                    !hud.AuctionModel!.Open && toast.TextId == AuctionTextIds.LevelLocked && toast.ArgValue == NumberText.Of(auctionLevel),
                    $"closed, {AuctionTextIds.LevelLocked} {auctionLevel}", $"{(hud.AuctionModel.Open ? "open" : "closed")}, '{toast.TextId}' {toast.ArgValue}");
                yield return Shot(step, "auction");
            }

            int stallLevel = UnlockLevelOf("stalls");
            if (level < stallLevel)
            {
                var stalls = feed.Stalls ?? throw new QaFailure("no stall places on the client");
                yield return BackToCityOutOfCombat(step);
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
                    "the free stall place in reach");
                yield return Settle();
                Check(step, "below the stalls level no stall prompt shows at a free place", hud.StallPrompt?.IsShown != true, "hidden",
                    hud.StallPrompt?.Text ?? "missing");
                yield return Press(_input.ButtonForPath(interact.Binding));
                yield return WaitUntil(() => toast.TextId == StallTextIds.LevelLocked, QaTimeouts.UiSeconds, "the stalls' level toast");
                Check(step, "below the stalls level the interaction key toasts the level and opens nothing",
                    !hud.StallModel!.Open && toast.TextId == StallTextIds.LevelLocked && toast.ArgValue == NumberText.Of(stallLevel),
                    $"closed, {StallTextIds.LevelLocked} {stallLevel}", $"{(hud.StallModel.Open ? "open" : "closed")}, '{toast.TextId}' {toast.ArgValue}");
                yield return Shot(step, "stall");
            }
            yield return Shot(step);
        }
    }
}
