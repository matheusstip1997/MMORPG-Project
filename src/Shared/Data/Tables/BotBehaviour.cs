using System;
using System.Collections.Generic;
using System.Linq;

namespace Shared.Data.Tables
{
    /// <summary>
    /// How a BotClient bot plays (backlog J, spec 11.5 "field combat with monsters"): it walks to its home spawn point,
    /// fights the nearest monster with its class kit the way the QA kill step does (normal attack, skills when ready, heal
    /// and back off when hurt), dodges some monster attacks, picks up drops, and after a death respawns and repeats.
    /// Tools only; not a game rule.
    /// </summary>
    [Table("BotBehaviour", 1)]
    public sealed class BotBehaviour : IValidatableRow, ISetValidatableRow
    {
        [TableId] public string Id { get; set; } = string.Empty;

        /// <summary>Spawn points the bots of a run are spread over, round robin by bot index.</summary>
        [Ref(typeof(SpawnPoints))] public string[] HomeMarkerIds { get; set; } = Array.Empty<string>();

        /// <summary>Monsters farther from the bot than this are not targets.</summary>
        [Range(100, 10_000)] public int TargetSearchCm { get; set; }

        /// <summary>The bot stops walking at this percent of its normal attack's reach (QA: 55).</summary>
        [Range(10, 100)] public int ApproachReachPercent { get; set; }

        /// <summary>
        /// R-2.2-07: a monster's body stops the bot at contact (the two body radii less the tolerance), so it stops walking
        /// at this distance past contact when that is farther than <see cref="ApproachReachPercent"/> (the boss's body is
        /// wider than a melee kit's approach).
        /// </summary>
        [Optional, Range(0, 500)] public int ApproachBodyMarginCm { get; set; } = 20;

        /// <summary>Below this percent of max HP it uses a ready heal on itself (QA: 50).</summary>
        [Range(0, 100)] public int HealBelowPercent { get; set; }

        /// <summary>Below this percent with no heal ready it walks away from its target for a while (QA: 20).</summary>
        [Range(0, 100)] public int BackOffBelowPercent { get; set; }

        [Range(0, 10_000)] public int BackOffDistanceCm { get; set; }

        /// <summary>The shortest back-off: before this it never walks back, recovered or not.</summary>
        [Range(0, 30_000)] public int BackOffMs { get; set; }

        /// <summary>
        /// After a back-off it fights again once HP is at least this percent of max HP or a self-heal is usable, and it
        /// starts no new fight below it (the QA kill step's rule, <c>QaFightPlan.Recovered</c>, QA: 40).
        /// </summary>
        [Optional, Range(0, 100)] public int ReengageAbovePercent { get; set; } = 40;

        /// <summary>It waits for <see cref="ReengageAbovePercent"/> this long at most, then fights at the HP it has (QA: 30 s).</summary>
        [Optional, Range(0, 600_000)] public int BackOffRecoverMs { get; set; } = 30_000;

        /// <summary>
        /// Plan each back-off leg as the QA kill step does (<c>QaNavigation.TryBackOffTarget</c>, WP-M2-12): it ends inside the
        /// threat's leash less <see cref="BackOffLeashMarginCm"/> around the threat's spawn point, on a clear straight walk,
        /// and outside every other spawn point's aggro range plus <see cref="AvoidAggroMarginCm"/>; else inside the leash only;
        /// else straight away. False: straight away from the nearest monster (the M1 bot).
        /// </summary>
        [Optional] public bool PlanBackOffLegs { get; set; }

        /// <summary>A planned leg ends this far inside the threat's <c>leashRangeCm</c> from its home, so it does not leash (QA: 400).</summary>
        [Optional, Range(0, 10_000)] public int BackOffLeashMarginCm { get; set; } = 400;

        /// <summary>A planned leg ends this far outside the other spawn points' <c>aggroRangeCm</c> (QA: 300).</summary>
        [Optional, Range(0, 10_000)] public int AvoidAggroMarginCm { get; set; } = 300;

        /// <summary>Chance to dodge when a monster within its reach starts an attack.</summary>
        [Range(0, 100)] public int DodgeChancePercent { get; set; }

        /// <summary>A bot reacts (dodge, next action) after a random delay in this range, so bots do not act in lockstep.</summary>
        [Range(0, 5_000)] public int ReactionMinMs { get; set; }

        [Range(0, 5_000)] public int ReactionMaxMs { get; set; }

        /// <summary>Loot drops within this distance are picked up (walk next to them first).</summary>
        [Range(0, 10_000)] public int PickupSearchCm { get; set; }

        /// <summary>It walks to this percent of <c>LootSettings.pickupRangeCm</c> before asking (QA: 40).</summary>
        [Range(10, 100)] public int PickupReachPercent { get; set; }

        /// <summary>
        /// After its own kill the bot loots before it fights on, for this long: drops are locked to the killer for a while
        /// (R-4.3-02), so the killer is the one that can take them.
        /// </summary>
        [Range(0, 60_000)] public int LootAfterKillMs { get; set; }

        /// <summary>A walk that moved less than <see cref="StuckMinMoveCm"/> in this time is re-planned.</summary>
        [Range(500, 60_000)] public int StuckTimeoutMs { get; set; }

        [Range(1, 1_000)] public int StuckMinMoveCm { get; set; }

        /// <summary>
        /// R-3.1-10: during a normal attack hit with a next hit, press the attack again once its chain window opens
        /// (<c>ActionDef.chainFromMs</c>; the machine starts the next hit at <c>chainLinkMs</c>), so the bot plays hits 2 to 4.
        /// False: only hit 1, as the M1 bots did (for comparisons).
        /// </summary>
        [Optional] public bool PressAttackChain { get; set; } = true;

        /// <summary>
        /// The field zone (WP-M2-7, spec 11.5): a bot's home is one of <see cref="HomeMarkerIds"/> whose monster's recommended
        /// level range (<c>MonsterDef.recommendedLevelMin/Max</c>) holds the bot's own level, chosen again at each level-up,
        /// so bots leveling together move from band to band. False: round robin over every home by bot index (M1).
        /// </summary>
        [Optional] public bool HomesByLevel { get; set; }

        /// <summary>
        /// The loop scenario (WP-M2-12, spec 11.5): the steps a bot plays in order, round after round, from
        /// <see cref="LoopStepIds"/>. Empty: the M1 fight-and-loot brain alone. An entry may carry an argument for its step,
        /// <c>"&lt;step&gt;:&lt;argument&gt;"</c> (WP-M3-12: <c>takeJob:blacksmith</c>, <c>buy:m3.pickaxeT1</c>, <c>level:12</c>);
        /// the step reads it (<see cref="TryParseLoopStep"/>).
        /// </summary>
        [Optional] public string[] LoopSteps { get; set; } = Array.Empty<string>();

        /// <summary>
        /// A script instead of a loop (WP-M3-12, the M3 exit run): <see cref="LoopSteps"/> play once, in order, and each
        /// entry's outcome is kept (done, not yet, failed, timed out). A script may repeat an entry and needs no fight step.
        /// </summary>
        [Optional] public bool LoopOnce { get; set; }

        /// <summary>The loop's fight step ends after this many own kills.</summary>
        [Optional, Range(1, 100)] public int LoopKillsPerRound { get; set; } = 1;

        /// <summary>The loop's pickup step ends when no own drop was seen for this long (or after <see cref="LootAfterKillMs"/>).</summary>
        [Optional, Range(0, 60_000)] public int LoopPickupIdleMs { get; set; } = 1_500;

        /// <summary>A loop step still running after this long is ended and counted as timed out.</summary>
        [Optional, Range(1_000, 3_600_000)] public int LoopStepTimeoutMs { get; set; } = 120_000;

        /// <summary>
        /// A script entry (<see cref="LoopOnce"/>) fails once the bot died more than this many times while it ran, with the
        /// deaths as its reason, instead of walking back from the respawn until the run's clock runs out (wave 1 QA fix run:
        /// the exit Blacksmith died about once a minute on the way to the wolves). 0: no limit; a loop never fails on deaths.
        /// </summary>
        [Optional, Range(0, 100)] public int LoopMaxDeathsPerEntry { get; set; }

        /// <summary>The loop's sell step asks for a new quote this many times after a <c>QuoteExpired</c>, then skips the stack.</summary>
        [Optional, Range(0, 10)] public int LoopSellQuoteRetries { get; set; } = 2;

        /// <summary>
        /// The loop's run step (WP-M2-6) builds runs of this many kill offers (the ones with the fewest kills), at most
        /// <c>QuestCaps.maxStepsPerRun</c>; the fight step's kills complete them and the run step claims them.
        /// </summary>
        [Optional, Range(1, Shared.Protocol.QuestMessages.MaxRunSteps)] public int LoopRunSteps { get; set; } = 2;

        /// <summary>What the loop's buy step buys at a vendor (a potion); empty: the buy step buys nothing.</summary>
        [Optional, Ref(typeof(ItemDef), Optional = true)] public string LoopBuyItemId { get; set; } = string.Empty;

        /// <summary>The buy step tops <see cref="LoopBuyItemId"/> in the Normal bag up to this many units, as far as the gold pays.</summary>
        [Optional, Range(0, 1_000)] public int LoopBuyKeep { get; set; }

        /// <summary>
        /// The loop's allocate step (WP-M2-13, R-3.4-08): per class, the attributes its free points go to, one entry
        /// <c>"&lt;ClassDef id&gt;:&lt;attr&gt;[,&lt;attr&gt;...]"</c> each (<c>"warrior:str,sta"</c>; attributes <c>str</c>, <c>dex</c>,
        /// <c>sta</c>, <c>int</c>). Each point goes to the listed attribute with the fewest placed points; a tier-1 class
        /// uses its root's entry; a class without one puts its points on its fastest-growing attribute. Placeholder builds.
        /// </summary>
        [Optional] public string[] LoopAttributeOrder { get; set; } = Array.Empty<string>();

        /// <summary>
        /// The loop's useItem step (WP-M3-3, spec 3.4 consumable use): below this percent of its max HP the bot drinks an HP
        /// potion it holds (one <c>UseItemRequest</c>); 0: the step never drinks. Placeholder.
        /// </summary>
        [Optional, Range(0, 100)] public int LoopDrinkBelowPercent { get; set; }

        /// <summary>
        /// The step ids the BotClient knows (its step registry has one step per id): fight, pickup, sell, buy (WP-M2-4), run
        /// (WP-M2-6), learn (WP-M2-5), allocate (WP-M2-13), rankUp (WP-M2-3) and equip (WP-M2-1); then the M3 steps
        /// (WP-M3-12, spec 11.5): level, takeJob (WP-M3-1), gather (WP-M3-5), refine, convert, craft (WP-M3-6), enchant
        /// (WP-M3-8), socket (WP-M3-9), useItem (WP-M3-3) and learnJob (WP-M3-7).
        /// </summary>
        public static readonly IReadOnlyList<string> LoopStepIds = new[]
        {
            "fight", "pickup", "sell", "buy", "run", "learn", "allocate", "rankUp", "equip",
            "level", "takeJob", "gather", "refine", "convert", "craft", "enchant", "socket", "useItem", "learnJob",
        };

        /// <summary>
        /// Reads one <see cref="LoopSteps"/> entry: the step id and its argument (empty without one). False for an empty id
        /// or an empty argument after the colon; the id is not checked against <see cref="LoopStepIds"/>.
        /// </summary>
        public static bool TryParseLoopStep(string entry, out string stepId, out string argument)
        {
            entry = entry ?? string.Empty;
            int colon = entry.IndexOf(':');
            stepId = (colon < 0 ? entry : entry.Substring(0, colon)).Trim();
            argument = colon < 0 ? string.Empty : entry.Substring(colon + 1).Trim();
            return stepId.Length > 0 && (colon < 0 || argument.Length > 0);
        }

        /// <summary>
        /// Reads one <see cref="LoopAttributeOrder"/> entry: the class id and the attributes (<c>Attributes.Str</c> ...
        /// <c>Attributes.Int</c>) in order. False for a malformed entry (no class, no attribute, an unknown or repeated one).
        /// </summary>
        public static bool TryParseAttributeOrder(string entry, out string classId, out int[] attributes)
        {
            classId = string.Empty;
            attributes = Array.Empty<int>();
            int colon = (entry ?? string.Empty).IndexOf(':');
            if (colon <= 0 || colon == entry!.Length - 1)
                return false;
            var list = new List<int>();
            foreach (var part in entry.Substring(colon + 1).Split(','))
            {
                int attribute = Array.IndexOf(Simulation.Stats.StatIds.Attributes, part.Trim());
                if (attribute < 0 || list.Contains(attribute))
                    return false;
                list.Add(attribute);
            }
            classId = entry.Substring(0, colon).Trim();
            attributes = list.ToArray();
            return classId.Length > 0;
        }

        public void Validate(RowErrors errors)
        {
            if (HomeMarkerIds.Length == 0)
                errors.Add(nameof(HomeMarkerIds), "needs at least one spawn point");
            if (BackOffBelowPercent > HealBelowPercent)
                errors.Add(nameof(BackOffBelowPercent), "must not be above HealBelowPercent");
            if (ReengageAbovePercent < BackOffBelowPercent || ReengageAbovePercent > HealBelowPercent)
                errors.Add(nameof(ReengageAbovePercent), "must be from BackOffBelowPercent to HealBelowPercent");
            if (BackOffRecoverMs < BackOffMs)
                errors.Add(nameof(BackOffRecoverMs), "must not be below BackOffMs");
            if (ReactionMinMs > ReactionMaxMs)
                errors.Add(nameof(ReactionMinMs), "must not be above ReactionMaxMs");
            if (LoopSteps.Length > 0)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                bool fight = false;
                foreach (var entry in LoopSteps)
                {
                    if (!TryParseLoopStep(entry, out string step, out _))
                        errors.Add(nameof(LoopSteps), $"'{entry}' is not '<step>' or '<step>:<argument>'");
                    else if (!LoopStepIds.Contains(step))
                        errors.Add(nameof(LoopSteps), $"'{step}' is not a loop step ({string.Join(", ", LoopStepIds)})");
                    else if (!seen.Add(entry) && !LoopOnce) // a script may play an entry twice (WP-M3-12)
                        errors.Add(nameof(LoopSteps), $"'{entry}' appears twice");
                    else
                        fight |= step == "fight";
                }
                if (!fight && !LoopOnce)
                    errors.Add(nameof(LoopSteps), "needs the fight step");
            }
            else if (LoopOnce)
            {
                errors.Add(nameof(LoopSteps), "a script (loopOnce) needs at least one step");
            }
            var classes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in LoopAttributeOrder)
            {
                if (!TryParseAttributeOrder(entry, out string classId, out _))
                    errors.Add(nameof(LoopAttributeOrder), $"'{entry}' is not '<class>:<attr>[,<attr>...]' with attributes str, dex, sta, int (each once)");
                else if (!classes.Add(classId))
                    errors.Add(nameof(LoopAttributeOrder), $"class '{classId}' is listed twice");
            }
        }

        /// <summary>
        /// With <see cref="HomesByLevel"/>, the homes' monsters cover every level from 1 to the highest recommended level among
        /// them, so a bot always has a camp (WP-M2-7). With <see cref="PlanBackOffLegs"/>, <see cref="BackOffLeashMarginCm"/> is
        /// below every home monster's leash range, so a planned leg can fit (WP-M2-12). Skipped when SpawnPoints or MonsterDef
        /// is not loaded.
        /// </summary>
        public void Validate(ConfigSet set, RowErrors errors)
        {
            // WP-M2-13: every class of LoopAttributeOrder is a ClassDef row.
            if (set.TryGet(typeof(ClassDef), out var classDefs))
            {
                foreach (var entry in LoopAttributeOrder)
                {
                    if (TryParseAttributeOrder(entry, out string classId, out _) && !classDefs.Rows.Cast<ClassDef>().Any(c => c.Id == classId))
                        errors.Add(nameof(LoopAttributeOrder), $"'{classId}' is not a ClassDef row");
                }
            }
            if (!set.TryGet(typeof(SpawnPoints), out var points) || !set.TryGet(typeof(MonsterDef), out _))
                return;
            if (PlanBackOffLegs)
            {
                // A planned leg must fit inside the leash of every monster the bot hunts at home.
                foreach (var home in HomeMarkerIds)
                {
                    var point = points.Rows.Cast<SpawnPoints>().FirstOrDefault(p => p.MarkerId == home);
                    if (point != null && MonsterDef.Find(set, point.MonsterId) is { } monster && monster.LeashRangeCm <= BackOffLeashMarginCm)
                    {
                        errors.Add(nameof(BackOffLeashMarginCm), $"must be below the leash range of {monster.Id} ({monster.LeashRangeCm} cm, home {home})");
                        break;
                    }
                }
            }
            if (!HomesByLevel)
                return;
            var ranges = new List<(int Min, int Max)>();
            foreach (var home in HomeMarkerIds)
            {
                var point = points.Rows.Cast<SpawnPoints>().FirstOrDefault(p => p.MarkerId == home);
                if (point != null && MonsterDef.Find(set, point.MonsterId) is { } monster)
                    ranges.Add((Math.Max(1, monster.RecommendedMin()), monster.RecommendedMax()));
            }
            if (ranges.Count == 0)
                return;
            int top = ranges.Max(r => r.Max);
            for (int level = 1; level <= top; level++)
            {
                if (!ranges.Any(r => r.Min <= level && level <= r.Max))
                {
                    errors.Add(nameof(HomeMarkerIds), $"with homesByLevel, no home's monster is recommended at level {level}");
                    return;
                }
            }
        }

        /// <summary>
        /// The homes for a bot of <paramref name="level"/> (null: not known yet, level 1) among <see cref="HomeMarkerIds"/>:
        /// those whose monster's recommended range holds the level, else those recommended for the highest levels below it.
        /// Without <see cref="HomesByLevel"/>, every home. <paramref name="recommended"/> gives a home's range, null for none.
        /// </summary>
        public IReadOnlyList<string> HomesFor(int? level, Func<string, (int Min, int Max)?> recommended)
        {
            if (!HomesByLevel)
                return HomeMarkerIds;
            int at = Math.Max(1, level ?? 1);
            var fitting = HomeMarkerIds.Where(h => recommended(h) is { } r && r.Min <= at && at <= r.Max).ToList();
            if (fitting.Count > 0)
                return fitting;
            var below = HomeMarkerIds.Where(h => recommended(h) is { } r && r.Max < at).ToList();
            if (below.Count == 0)
                return HomeMarkerIds;
            int best = below.Max(h => recommended(h)!.Value.Max);
            return below.Where(h => recommended(h)!.Value.Max == best).ToList();
        }
    }
}
