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
    public sealed class BotBehaviour : IValidatableRow
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
        /// The loop scenario (WP-M2-12, spec 11.5): the steps a bot plays in order, round after round, from
        /// <see cref="LoopStepIds"/>. Empty: the M1 fight-and-loot brain alone.
        /// </summary>
        [Optional] public string[] LoopSteps { get; set; } = Array.Empty<string>();

        /// <summary>The loop's fight step ends after this many own kills.</summary>
        [Optional, Range(1, 100)] public int LoopKillsPerRound { get; set; } = 1;

        /// <summary>The loop's pickup step ends when no own drop was seen for this long (or after <see cref="LootAfterKillMs"/>).</summary>
        [Optional, Range(0, 60_000)] public int LoopPickupIdleMs { get; set; } = 1_500;

        /// <summary>A loop step still running after this long is ended and counted as timed out.</summary>
        [Optional, Range(1_000, 3_600_000)] public int LoopStepTimeoutMs { get; set; } = 120_000;

        /// <summary>
        /// The step ids the BotClient knows (its step registry has one step per id; the M2 WPs fill in the placeholders):
        /// fight and pickup play today; sell, buy (WP-M2-4), run (WP-M2-6), learn (WP-M2-5) and equip (WP-M2-1) are
        /// placeholders until their protocol lands.
        /// </summary>
        public static readonly IReadOnlyList<string> LoopStepIds = new[] { "fight", "pickup", "sell", "buy", "run", "learn", "equip" };

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
                foreach (var step in LoopSteps)
                {
                    if (!LoopStepIds.Contains(step))
                        errors.Add(nameof(LoopSteps), $"'{step}' is not a loop step ({string.Join(", ", LoopStepIds)})");
                    else if (!seen.Add(step))
                        errors.Add(nameof(LoopSteps), $"'{step}' appears twice");
                }
                if (!seen.Contains("fight"))
                    errors.Add(nameof(LoopSteps), "needs the fight step");
            }
        }
    }
}
