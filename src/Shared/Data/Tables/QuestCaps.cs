using Shared.Protocol;

namespace Shared.Data.Tables
{
    /// <summary>
    /// Run caps (R-5.5-03; spec 5.5 "M2 parameters", user decision 2026-10-27): one active run per character of up to five steps.
    /// Placeholders in data. Party runs (M4) will need their own caps.
    /// </summary>
    [Table("QuestCaps", 1)]
    [Singleton]
    public sealed class QuestCaps : ISetValidatableRow
    {
        /// <summary>
        /// Runs a character holds that are not claimed or abandoned. M2 supports exactly one: the run requests name no run, the
        /// tracker shows one. More need a run id on the wire (TODO(M4) with party runs).
        /// </summary>
        [Range(1, 1)] public int MaxActiveRuns { get; set; }

        /// <summary>Steps one run may bundle (at most the board's offers, since each step is a distinct offer).</summary>
        [Range(1, QuestMessages.MaxRunSteps)] public int MaxStepsPerRun { get; set; }

        /// <summary>Quest run requests (accept, deliver, forfeit, claim) one session may have waiting for the world (R-2.5-03).</summary>
        [Range(1, 64)] public int MaxQueuedRequests { get; set; }

        public void Validate(ConfigSet set, RowErrors errors)
        {
            if (set.TryGet(typeof(QuestBoardSettings), out var table) && table.Rows.Count == 1
                && table.Rows[0] is QuestBoardSettings board && MaxStepsPerRun > board.OffersPerBoard)
                errors.Add(nameof(MaxStepsPerRun), $"{MaxStepsPerRun} steps need as many distinct offers; the board has {board.OffersPerBoard}");
        }
    }

    /// <summary>
    /// The outpost quest board (R-5.5-01, spec 5.5): <see cref="OffersPerBoard"/> offers per outpost NPC, generated from the
    /// demand state when the board epoch (world clock / <see cref="RefreshMs"/>) changes and cached until the next one. The
    /// outpost's zone is every monster spawn point within <see cref="RadiusCm"/> of the NPC. Placeholders in data.
    /// </summary>
    [Table("QuestBoardSettings", 1)]
    [Singleton]
    public sealed class QuestBoardSettings
    {
        [Range(1, QuestMessages.MaxOffers)] public int OffersPerBoard { get; set; }

        /// <summary>A board lives this long; accepting needs the current one (<c>BoardExpired</c>).</summary>
        [Range(60_000, 86_400_000)] public int RefreshMs { get; set; }

        /// <summary>Spawn points within this distance of the outpost NPC supply the board's monsters.</summary>
        [Range(100, 100_000_000)] public int RadiusCm { get; set; }

        /// <summary>Whether bosses (<c>MonsterDef.boss</c>) may be named by an offer.</summary>
        public bool IncludeBosses { get; set; }
    }

    /// <summary>
    /// The run's step bonus (R-5.5-03 "more steps = bigger bonus"; user decision 2026-10-27): +<see cref="PerStepPercent"/> per
    /// completed step beyond the first, at most <see cref="CapPercent"/>. Forfeited steps do not count. Placeholders in data.
    /// </summary>
    [Table("RunBonus", 1)]
    [Singleton]
    public sealed class RunBonus
    {
        [Range(0, 1000)] public int PerStepPercent { get; set; }

        [Range(0, 1000)] public int CapPercent { get; set; }
    }

    /// <summary>
    /// The forfeit penalty (R-5.5-03; user decisions 2026-10-27): a forfeited step pays nothing and takes
    /// <see cref="PerForfeitPercent"/> off the whole run, per forfeited step, at most <see cref="CapPercent"/>. Placeholders in data.
    /// </summary>
    [Table("ForfeitPenalty", 1)]
    [Singleton]
    public sealed class ForfeitPenalty
    {
        [Range(0, 100)] public int PerForfeitPercent { get; set; }

        [Range(0, 100)] public int CapPercent { get; set; }
    }
}
