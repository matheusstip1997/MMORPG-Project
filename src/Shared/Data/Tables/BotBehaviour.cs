using System;

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

        /// <summary>Below this percent of max HP it uses a ready heal on itself (QA: 50).</summary>
        [Range(0, 100)] public int HealBelowPercent { get; set; }

        /// <summary>Below this percent with no heal ready it walks away from its target for a while (QA: 20).</summary>
        [Range(0, 100)] public int BackOffBelowPercent { get; set; }

        [Range(0, 10_000)] public int BackOffDistanceCm { get; set; }

        [Range(0, 30_000)] public int BackOffMs { get; set; }

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

        public void Validate(RowErrors errors)
        {
            if (HomeMarkerIds.Length == 0)
                errors.Add(nameof(HomeMarkerIds), "needs at least one spawn point");
            if (BackOffBelowPercent > HealBelowPercent)
                errors.Add(nameof(BackOffBelowPercent), "must not be above HealBelowPercent");
            if (ReactionMinMs > ReactionMaxMs)
                errors.Add(nameof(ReactionMinMs), "must not be above ReactionMaxMs");
        }
    }
}
