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
using Shared.World;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Dev.Qa
{
    /// <summary>The death loot bag steps (WP-M4-9, spec 4.3 R-4.3-03, 4.4 R-4.4-01, 9.13).</summary>
    public sealed partial class QaScenario
    {
        /// <summary>
        /// The partner scripts of <c>loot-bag-stolen</c>: take and try to sell (<c>BotBehaviour</c> row <c>qa-partner-loot-bag-stolen</c>)
        /// when the drop holds a sell-only unit, else take only (<c>qa-partner-loot-bag-stolen-take</c>): an outpost buys sell-only items
        /// only, so a stolen material has no sale to refuse.
        /// </summary>
        private const string StolenScript = "loot-bag-stolen", StolenTakeScript = "loot-bag-stolen-take";

        /// <summary>The Normal bag's <c>ContainerDef.code</c> (the run result's <c>bagContainer</c> default).</summary>
        private const int NormalBagCode = 1;

        /// <summary>
        /// WP-M4-9 (R-4.3-03, 9.13): a death in <c>m1.combat</c> (its <c>deathDrop</c> on) drops the whole unreserved Normal bag in a
        /// loot bag. The player dies with the dev console's <c>/die</c> typed in chat where it stands (out of the demon's aggro after
        /// <c>wait-respawn</c>), the own bag list (<c>OwnLootBags</c>) holds the new bag with every unit and the saved Normal bag keeps
        /// only what was reserved; the player respawns in the city, walks back, the pickup prompt says "open the bag", the pickup key
        /// opens the bag window (own bag), Take all clicked through the pointer is answered after its commit, the bag leaves the list
        /// and the saved Normal bag holds every unit again. n/a with an empty Normal bag.
        /// </summary>
        private IEnumerator DieAndRecover(QaStepResult step)
        {
            var (feed, dev) = LootBagRig(step);
            yield return WaitUntil(() => feed.OwnLootBags != null && _tap.Inventory != null, QaTimeouts.UiSeconds,
                "the own bag list and the saved bag");
            int before = NormalUnits(reserved: false);
            if (before == 0)
                throw new QaNotApplicable("the Normal bag holds nothing unreserved to drop");
            // QA M4 wave 2: an earlier run's take-stolen may have left a stolen stack; the own take must mark nothing more.
            int stolenBefore = StolenUnits();
            yield return ToDeathDropSpot(step); // QA M4 wave 1 F1: never where the crafting steps left the player (the city)
            var known = new HashSet<uint>(feed.OwnLootBags!.Bags.Select(b => b.BagEntityId));
            step.log.Add($"{before} unreserved units in the Normal bag, {known.Count} own bags lying; dying at ({_tap.OwnXCm}, {_tap.OwnYCm})");

            OwnLootBagEntry? bag = null;
            yield return DieByConsole(step, feed);
            yield return WaitUntil(() => (bag = NewOwnBag(feed, known)) != null, QaTimeouts.LootSeconds, "the new bag in the own list");
            Check(step, "the death dropped the Normal bag in one bag", bag!.Units == before, NumberText.Of(before) + " units",
                NumberText.Of(bag.Units) + " units");
            yield return WaitUntil(() => NormalUnits(reserved: false) == 0, QaTimeouts.LootSeconds, "the saved Normal bag without the dropped units");
            Check(step, "the saved Normal bag keeps nothing unreserved", NormalUnits(reserved: false) == 0, "0", NumberText.Of(NormalUnits(reserved: false)));
            yield return Shot(step, "dead");

            var starting = _config.Single<StartingValuesM1>();
            yield return WaitUntil(() => !OwnDead(), starting.PlayerRespawnDelayMs / 1000f + QaTimeouts.RespawnMarginSeconds,
                $"the player's respawn (playerRespawnDelayMs {starting.PlayerRespawnDelayMs})");
            yield return OpenOwnBag(step, feed, bag, () => PickupBlockedDuringTrade(step, feed, dev)); // QA M4 wave 1: the pickup key during a trade

            var model = _hud!.LootBagModel!;
            var window = _hud.LootBagWindow!;
            Check(step, "the bag window shows the own bag", model.Content?.Access == LootBagAccess.Own && window.IsShown,
                "Own, shown", $"{model.Content?.Access}, {(window.IsShown ? "shown" : "hidden")}");
            CheckWindowPlace(step, HudElementIds.LootBagWindow);
            var answers = new List<LootBagResult>();
            void OnAnswer(LootBagResult result) => answers.Add(result);
            feed.LootBagAnswered += OnAnswer;
            var panel = window.Window.panel ?? throw new QaFailure("the bag window is on no panel");
            var mask = new MaskWatch();
            try
            {
                yield return Shot(step, "window");
                yield return PointerTo(panel, QaUiPointer.Center(window.TakeAllButton), QaTimeouts.PointerSteps, dev, mask);
                yield return PointerClick(panel, dev, mask);
                yield return WaitUntil(() => answers.Any(a => a.Op == LootBagOp.TakeAll), QaTimeouts.LootSeconds,
                    "Take all's answer (after its commit)");
                var answer = answers.Last(a => a.Op == LootBagOp.TakeAll);
                Check(step, "Take all answered after its commit, nothing refused, nothing marked",
                    answer.Reason == LootBagReason.None && answer.Taken + answer.Mailed == bag.Units && !answer.Stolen,
                    $"{bag.Units} units back", $"{answer.Reason}, {answer.Taken} taken, {answer.Mailed} mailed, stolen {answer.Stolen}");
            }
            finally
            {
                feed.LootBagAnswered -= OnAnswer;
            }
            uint bagId = bag.BagEntityId;
            yield return WaitUntil(() => feed.OwnLootBags!.Bags.All(b => b.BagEntityId != bagId) && NormalUnits(reserved: false) >= before,
                QaTimeouts.LootSeconds, "the bag gone from the own list and the saved Normal bag refilled");
            Check(step, "the bag left the own list", feed.OwnLootBags!.Bags.All(b => b.BagEntityId != bagId), "gone", "gone");
            Check(step, "the saved Normal bag holds every unit again", NormalUnits(reserved: false) == before, NumberText.Of(before),
                NumberText.Of(NormalUnits(reserved: false)));
            Check(step, "nothing came back stolen", StolenUnits() == stolenBefore, NumberText.Of(stolenBefore) + " stolen units, as before the death",
                NumberText.Of(StolenUnits()) + " stolen units");
            Check(step, "clicks in the bag window start nothing", mask.Clean, "predicted machine idle", mask.Describe());
            RecordMovedStackAfterRecovery(step);
            yield return WaitUntil(() => !model.Open, QaTimeouts.UiSeconds, "the emptied bag's window to close");
            yield return PointerTo(panel, new Vector2(640f, 300f), QaTimeouts.PointerSteps);
            yield return Settle();
            yield return Shot(step);
        }

        /// <summary>
        /// WP-M4-9 (R-4.3-03, R-4.4-01, R-4.4-02) with the QA partner bot (not in the player's party): the player asks the partner
        /// for its script <c>loot-bag-stolen</c>, dies where it stands (<c>/die</c>) and posts its bag on the pair board
        /// (<c>bag.at</c>, the bot's <c>die</c> format); the partner (<c>recoverBag:partner</c>) walks there, is refused inside
        /// the owner window and takes everything after it with the stolen mark; with a sell-only unit in the drop <c>sell:stolen</c> is then refused its sale with
        /// <c>ItemStolen</c>. The player sees its bag leave the own list before its expiry and the partner's script answer done.
        /// n/a without the partner bot or with an empty Normal bag.
        /// </summary>
        private IEnumerator LootBagStolen(QaStepResult step)
        {
            var partner = _partner ?? throw new QaNotApplicable(QaPartner.NeededReason);
            var (feed, _) = LootBagRig(step);
            yield return WaitUntil(() => feed.OwnLootBags != null && _tap.Inventory != null && !OwnDead(), QaTimeouts.UiSeconds,
                "the own bag list, the saved bag and the player alive");
            int units = NormalUnits(reserved: false);
            if (units == 0)
                throw new QaNotApplicable("the Normal bag holds nothing unreserved to drop");
            var known = new HashSet<uint>(feed.OwnLootBags!.Bags.Select(b => b.BagEntityId));
            yield return ToDeathDropSpot(step); // QA M4 wave 1 F1, before the partner is asked for anything
            var link = partner.Link;
            bool sellable = _tap.Inventory!.Slots.Any(s => !s.Reserved && Rows<ItemDef>().Any(d => d.NetId == s.ItemNetId && d.Category == ItemCategory.SellOnly));
            string script = sellable ? StolenScript : StolenTakeScript;
            int request = link.Request(script); // first: the bot's step ignores a bag posted before it began
            step.log.Add($"asked the partner for '{script}' (request {request}{(sellable ? "" : ", no sell-only unit to sell")}); {units} units to drop at ({_tap.OwnXCm}, {_tap.OwnYCm})");
            yield return Wait(QaTimeouts.PartnerRequestPickupSeconds);

            OwnLootBagEntry? bag = null;
            yield return DieByConsole(step, feed);
            yield return WaitUntil(() => (bag = NewOwnBag(feed, known)) != null, QaTimeouts.LootSeconds, "the new bag in the own list");
            link.Post("bag.at", $"{bag!.BagEntityId},{bag.XCm},{bag.YCm}");
            Check(step, "the death dropped the Normal bag", bag.Units == units, NumberText.Of(units), NumberText.Of(bag.Units));
            yield return Shot(step, "dropped");

            uint bagId = bag.BagEntityId;
            float windowSeconds = (bag.OwnerWindowLeftMs + 999) / 1000f;
            yield return WaitUntil(() => feed.OwnLootBags!.Bags.All(b => b.BagEntityId != bagId),
                windowSeconds + QaTimeouts.PartnerSeconds, "the partner's take (after the owner window)");
            Check(step, "the bag left the own list before its expiry (taken)", feed.OwnLootBags!.Bags.All(b => b.BagEntityId != bagId),
                "gone", "gone");
            if (_writer.Result.bagItemId.Length > 0 && _writer.Result.bagContainer == NormalBagCode)
                _writer.Result.bagStolen = true; // the saved-bag phase now checks that the moved stack left with the bag
            string outcome = "", text = "";
            yield return WaitUntil(() => partner.TryResult(request, out outcome, out text), QaTimeouts.PartnerSeconds * 2,
                $"the partner bot's answer to its script '{script}'");
            Check(step, sellable ? $"the partner's script '{script}' done (stolen mark set, the sale refused ItemStolen)"
                : $"the partner's script '{script}' done (stolen mark set)",
                outcome == PairScriptResult.Done, PairScriptResult.Done, text);
            yield return Shot(step, "taken");
            // The steps after this one start where the player died (out of the demon's aggro in m1.combat), not at the city respawn.
            var starting = _config.Single<StartingValuesM1>();
            yield return WaitUntil(() => !OwnDead(), starting.PlayerRespawnDelayMs / 1000f + QaTimeouts.RespawnMarginSeconds, "the player's respawn");
            yield return WalkTo(FixVec2.FromCm(bag.XCm, bag.YCm), _tap.Hits.Count);
            yield return Settle();
            yield return Shot(step);
        }

        /// <summary>
        /// die-and-recover returned the Normal bag through the take, so the stack move-stack moved may now lie in another slot (merged
        /// with its kind): the saved-bag phase checks where the client was told it is now.
        /// </summary>
        private void RecordMovedStackAfterRecovery(QaStepResult step)
        {
            var result = _writer.Result;
            if (result.bagItemId.Length == 0 || result.bagContainer != NormalBagCode)
                return;
            var def = Rows<ItemDef>().FirstOrDefault(d => d.Id == result.bagItemId);
            var slot = def == null ? null : _tap.Inventory!.Slots.Where(s => s.ItemNetId == def.NetId).OrderBy(s => s.Slot).FirstOrDefault();
            if (slot == null)
                return;
            step.log.Add($"the moved {result.bagItemId} is back in slot {slot.Slot} x{slot.Count} (was slot {result.bagSlot} x{result.bagCount})");
            result.bagSlot = slot.Slot;
            result.bagCount = slot.Count;
            result.bagFromSlot = -1;
        }

        /// <summary>
        /// QA M4 wave 1 F1: the loot bag steps die in a region whose <c>deathDrop</c> is on (R-4.3-03), whatever the steps before left:
        /// after the crafting steps the player stood in <c>m1.city</c> (<c>normalCity</c>, deathDrop off) and no bag could drop. It
        /// walks, around every camp's aggro, to <see cref="QaNavigation.TryDeathDropSpot"/>'s point on the line from the camp the run
        /// fought toward the city spawn, just outside that camp's aggro, then checks the region it stands in and stops the step with
        /// the region's id, area type and flag when its death would drop nothing. The game rule is not touched.
        /// </summary>
        /// <param name="clearOfBags">QA M4 wave 2 (<c>take-stolen</c>): also keep <see cref="QaTimeouts.BagClearanceCm"/> from every loot bag
        /// in view, so a bag an earlier run left there (bags lie <c>BagExpiry.expiryMs</c>) is not the one the pickup prompt names.</param>
        private IEnumerator ToDeathDropSpot(QaStepResult step, bool clearOfBags = false)
        {
            string directory = ClientWorld.Directory("m1");
            var markers = QaMarkers.Load(directory);
            string spawnId = _config.Single<ServerLoop>().PlayerSpawnMarkerId;
            var city = markers.FirstOrDefault(m => m.id == spawnId) ?? throw new QaFailure("no player spawn marker " + spawnId);
            var drops = QaRegions.DeathDrop(directory, _config);
            var camps = new List<(QaMarker Marker, int AggroCm)>();
            foreach (var point in Rows<SpawnPoints>())
            {
                var marker = markers.FirstOrDefault(m => m.id == point.MarkerId);
                var def = MonsterDef.Find(_config, point.MonsterId);
                if (marker != null && def != null)
                    camps.Add((marker, def.AggroRangeCm + QaTimeouts.AvoidAggroMarginCm));
            }
            var cityAt = FixVec2.FromCm(city.xCm, city.yCm);
            // The camp the run fought (wait-respawn's), else the camp nearest the city in a death-drop region.
            var camp = camps.FirstOrDefault(c => c.Marker.id == _monsterMarker?.id);
            if (camp.Marker == null)
            {
                camp = camps.Where(c => drops[_grid.RegionIndexAt(FixVec2.FromCm(c.Marker.xCm, c.Marker.yCm))])
                    .OrderBy(c => DistanceCm(cityAt, FixVec2.FromCm(c.Marker.xCm, c.Marker.yCm))).FirstOrDefault();
            }
            if (camp.Marker == null)
                throw new QaFailure("no monster camp in a region whose deathDrop is on (R-4.3-03)");
            var campAt = FixVec2.FromCm(camp.Marker.xCm, camp.Marker.yCm);
            var threats = camps.Select(c => (At: FixVec2.FromCm(c.Marker.xCm, c.Marker.yCm), RadiusCm: c.AggroCm)).ToList();
            var bags = new List<(FixVec2 At, int RadiusCm)>();
            if (clearOfBags)
            {
                foreach (var entity in _tap.Registry.Entities.Values)
                {
                    if (entity.Kind == EntityKind.LootBag && QaWorldTap.TryGroundCm(entity, out int bx, out int by))
                        bags.Add((FixVec2.FromCm(bx, by), QaTimeouts.BagClearanceCm));
                }
                if (bags.Count > 0)
                    step.log.Add($"{bags.Count} loot bag(s) in view: the spot keeps {QaTimeouts.BagClearanceCm} cm from them");
            }
            if (!QaNavigation.TryDeathDropSpot(_grid, drops, campAt, camp.AggroCm + _grid.CellSizeCm, cityAt, threats.Concat(bags).ToList(),
                    QaTimeouts.DeathSpotSearchCm, out var spot))
                throw new QaFailure($"no walkable point in a deathDrop region outside the camps' aggro near {camp.Marker.id}");
            step.log.Add($"death spot ({FixedPoint.ToCmRound(spot.X)}, {FixedPoint.ToCmRound(spot.Y)}) by {camp.Marker.id}, out of its aggro; "
                         + $"walking from ({_tap.OwnXCm}, {_tap.OwnYCm})");
            var own = Own();
            var safe = QaNavigation.AvoidingThreats(_grid, threats.Where(t => DistanceCm(own, t.At) > t.RadiusCm)); // not the circle it stands in
            if (DistanceCm(own, spot) > QaTimeouts.DeathSpotArrivedCm)
                yield return WalkTo(spot, _tap.Hits.Count, safe);
            yield return Settle();
            int region = _grid.RegionIndexAt(Own());
            string regionId = QaRegions.IdOf(directory, region);
            var flags = QaRegions.AreaFlags(_config, regionId);
            Check(step, "the death spot is in a deathDrop region (R-4.3-03)", flags != null && flags.DeathDrop == DeathDrop.On,
                "deathDrop On", $"region {region} '{regionId}', area type '{flags?.Id ?? "?"}', deathDrop {(flags != null ? flags.DeathDrop.ToString() : "?")} at ({_tap.OwnXCm}, {_tap.OwnYCm})");
            Require(step);
        }

        private (WorldClientFeed feed, PredictionDevClient dev) LootBagRig(QaStepResult step)
        {
            var dev = UnityEngine.Object.FindAnyObjectByType<PredictionDevClient>();
            Check(step, "bag window built", _hud?.LootBagWindow != null && _hud.LootBagModel != null && dev?.Feed != null,
                "HudRoot.LootBagWindow", _hud?.LootBagWindow != null ? "built" : "missing");
            Require(step);
            return (dev!.Feed!, dev);
        }

        /// <summary>The saved Normal bag's stolen units (R-4.4-01).</summary>
        private int StolenUnits() => _tap.Inventory!.Slots.Where(s => s.Stolen || s.StolenMark != 0).Sum(s => s.Count);

        /// <summary>The saved Normal bag's units (the reserved stacks stay with the character; R-4.3-03).</summary>
        private int NormalUnits(bool reserved) => _tap.Inventory!.Slots.Where(s => s.Reserved == reserved).Sum(s => s.Count);

        private static OwnLootBagEntry? NewOwnBag(WorldClientFeed feed, HashSet<uint> known) =>
            feed.OwnLootBags?.Bags.FirstOrDefault(b => !known.Contains(b.BagEntityId));

        /// <summary>Types the dev console's <c>/die</c> in chat (spec 11.7) and waits for the death.</summary>
        private IEnumerator DieByConsole(QaStepResult step, WorldClientFeed feed)
        {
            var model = _hud!.Models!.Chat;
            var field = _hud.GetComponent<UIDocument>().rootVisualElement.Q<TextField>("chat_input")
                        ?? throw new QaFailure("no chat_input field in the HUD");
            var answers = new List<DevCommandResult>();
            void OnAnswer(DevCommandResult result) => answers.Add(result);
            feed.DevCommandAnswered += OnAnswer;
            try
            {
                yield return Press(_input!.Keyboard.enterKey);
                yield return WaitUntil(() => model.InputOpen, QaTimeouts.UiSeconds, "the chat input to open");
                QaUiKeys.Press(field, KeyCode.Return, '\n'); // the press that opened the input reaches the field too (see ChatEcho)
                yield return null;
                if (!QaUiKeys.Type(field, "/die"))
                    throw new QaFailure("the chat input has no focus for /die");
                yield return null;
                yield return Press(_input.Keyboard.enterKey);
                yield return WaitUntil(() => answers.Count > 0 || OwnDead(), QaTimeouts.UiSeconds,
                    "the dev console's answer to /die (MMORPG_DEV_TOOLS=1, a dev-login account)");
                if (answers.Count > 0 && answers[0].Reason != DevReason.None)
                    throw new QaFailure($"/die refused: {answers[0].Reason} {string.Join(" ", answers[0].Lines)}");
                yield return WaitUntil(OwnDead, QaTimeouts.UiSeconds, "the death after /die");
                step.log.Add($"/die: dead at ({_tap.OwnXCm}, {_tap.OwnYCm})");
            }
            finally
            {
                feed.DevCommandAnswered -= OnAnswer;
            }
        }

        /// <summary>Walks to the own bag until its prompt stands there, then opens it with the pickup key.</summary>
        private IEnumerator OpenOwnBag(QaStepResult step, WorldClientFeed feed, OwnLootBagEntry bag, Func<IEnumerator>? beforePickup = null,
            bool waitForWindow = true)
        {
            int rangeCm = _config.Single<LootSettings>().PickupRangeCm;
            var at = FixVec2.FromCm(bag.XCm, bag.YCm);
            step.log.Add($"walking from ({_tap.OwnXCm}, {_tap.OwnYCm}) to the bag {bag.BagEntityId} at ({bag.XCm}, {bag.YCm})");
            yield return WalkTo(at, _tap.Hits.Count);
            var prompt = _hud!.PickupPrompt;
            float from = Time.realtimeSinceStartup;
            while ((prompt == null || prompt.DropId != bag.BagEntityId) && Time.realtimeSinceStartup - from < QaTimeouts.LootSeconds)
            {
                if (DistanceCm(Predicted(), at) > rangeCm * QaTimeouts.LootApproachPercentOfRange / 100)
                    Walk(at);
                else
                    Walk(null);
                yield return null;
            }
            Walk(null);
            string promptText = _login.Text.Get(HudTextIds.BagPrompt, new TextArg("key", feed.PickupKeyName));
            Check(step, "the prompt at the bag says open the bag", prompt != null && prompt.DropId == bag.BagEntityId && prompt.Text == promptText,
                $"\"{promptText}\" at bag {bag.BagEntityId}", prompt == null ? "no HUD prompt" : $"\"{prompt.Text}\" at {prompt.DropId}");
            Require(step);
            yield return Shot(step, "prompt");
            var model = _hud.LootBagModel!;
            if (beforePickup != null)
                yield return beforePickup();
            yield return Press(_input!.ButtonFor("Pickup"));
            if (!waitForWindow)
                yield break; // take-stolen: a bag still in its owner window is refused (BagLocked), no window opens
            yield return WaitUntil(() => model.Open && model.BagEntityId == bag.BagEntityId, QaTimeouts.UiSeconds,
                "the bag window with the bag's contents");
            yield return Settle();
        }
    }
}
