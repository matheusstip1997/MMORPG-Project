namespace Shared.Data.Tables
{
    /// <summary>The forge minigame a job plays (spec 5.3 <c>IForgeChallenge</c>, M3 decision 12). Config enum; append only.</summary>
    public enum ForgeChallengeKind : byte
    {
        /// <summary>No minigame: the session's minigame term is 0 (the Jeweler in M3).</summary>
        None = 0,
        /// <summary>A timing bar on one forge action (the Blacksmith's hit in M3; WP-M3-10).</summary>
        TimingBar = 1,
    }

    /// <summary>
    /// The forge minigame of a job (spec 5.3 <c>IForgeChallenge</c>, R-5.3-02, M3 decision 12; WP-M3-10), one row per
    /// <see cref="JobDef"/>: <see cref="ForgeChallengeKind.None"/>, or a timing bar opened by each use of
    /// <see cref="ActionId"/>. Per round the server draws the marker's speed and the target's centre from the session seed;
    /// a press scores up to <see cref="HitMaxScore"/> inside the window (<see cref="HalfWidthPermille"/> either side of the
    /// centre), 0 outside it. Positions are in permille of the bar. A press is judged on the server's timeline: its lag (the
    /// server time since it issued the round minus the client's offset) must be at most the round trip plus
    /// <see cref="LatencyToleranceMs"/> and at most <see cref="MaxLagMs"/> (user answer in the WP-M3-10 plan, 2026-10-31: the
    /// forge has its own ceiling, since a forge input affects no other player; rule 12's 200 ms cap stays for combat).
    /// A session's minigame term is at most <see cref="MaxScore"/>, itself within <c>QualityCaps</c> minigame. Every number
    /// is a placeholder. Catalog 8.5.
    /// </summary>
    [Table("ForgeChallengeDef", 1)]
    public sealed class ForgeChallengeDef : IValidatableRow, ISetValidatableRow
    {
        /// <summary>Positions on the bar run from 0 to this (permille).</summary>
        public const int BarPermille = 1_000;

        /// <summary>The job (one row each, <see cref="ForgeCoverage"/>).</summary>
        [TableId, Ref(typeof(JobDef))] public string Id { get; set; } = string.Empty;

        public ForgeChallengeKind Kind { get; set; }

        /// <summary>The forge action whose every use opens a round (a timing bar); empty for none.</summary>
        [Optional, Ref(typeof(ForgeActionDef), Optional = true)] public string ActionId { get; set; } = string.Empty;

        /// <summary>The marker's slowest and fastest speed, permille of the bar per second (drawn per round).</summary>
        [Optional, Range(0, 100_000)] public int SpeedMinPermillePerSec { get; set; }

        [Optional, Range(0, 100_000)] public int SpeedMaxPermillePerSec { get; set; }

        /// <summary>Where the target's centre may fall (drawn per round), permille.</summary>
        [Optional, Range(0, BarPermille)] public int TargetMinPermille { get; set; }

        [Optional, Range(0, BarPermille)] public int TargetMaxPermille { get; set; }

        /// <summary>The window either side of the centre; a press outside it scores 0.</summary>
        [Optional, Range(0, BarPermille / 2)] public int HalfWidthPermille { get; set; }

        /// <summary>How long a round lasts on the client's clock; a press after it is late.</summary>
        [Optional, Range(0, 60_000)] public int DurationMs { get; set; }

        /// <summary>Lag beyond the measured round trip a press may have (at most <c>StartingValuesM1.lagCompensationCapMs</c>).</summary>
        [Optional, Range(0, 1_000)] public int LatencyToleranceMs { get; set; }

        /// <summary>The most lag a press may have whatever the round trip (at least <c>lagCompensationCapMs</c>).</summary>
        [Optional, Range(0, 5_000)] public int MaxLagMs { get; set; }

        /// <summary>The score of a press on the target's centre.</summary>
        [Optional, Range(0, 65_535)] public int HitMaxScore { get; set; }

        /// <summary>The most a session's rounds add up to (within <c>QualityCaps</c> minigame).</summary>
        [Optional, Range(0, 65_535)] public int MaxScore { get; set; }

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
            if (Kind == ForgeChallengeKind.None)
            {
                if (ActionId.Length > 0)
                    errors.Add(nameof(ActionId), "a job without a challenge names no action");
                return;
            }
            if (Kind != ForgeChallengeKind.TimingBar)
            {
                errors.Add(nameof(Kind), $"{Kind} is not a challenge kind");
                return;
            }
            if (ActionId.Length == 0)
                errors.Add(nameof(ActionId), "a timing bar names the forge action that opens it");
            if (SpeedMinPermillePerSec <= 0)
                errors.Add(nameof(SpeedMinPermillePerSec), "must be positive");
            if (SpeedMaxPermillePerSec < SpeedMinPermillePerSec)
                errors.Add(nameof(SpeedMaxPermillePerSec), "must be at least SpeedMinPermillePerSec");
            if (TargetMaxPermille < TargetMinPermille)
                errors.Add(nameof(TargetMaxPermille), "must be at least TargetMinPermille");
            if (HalfWidthPermille <= 0)
                errors.Add(nameof(HalfWidthPermille), "must be positive");
            if (TargetMinPermille - HalfWidthPermille < 0 || TargetMaxPermille + HalfWidthPermille > BarPermille)
                errors.Add(nameof(HalfWidthPermille), "the window must stay on the bar (0..1000 permille)");
            // One full sweep at the slowest speed, so every round passes its target at least once.
            if (DurationMs <= 0 || (long)SpeedMinPermillePerSec * DurationMs < (long)BarPermille * 1_000)
                errors.Add(nameof(DurationMs), "must give the slowest marker at least one full sweep of the bar");
            if (MaxLagMs <= 0)
                errors.Add(nameof(MaxLagMs), "must be positive");
            if (HitMaxScore <= 0)
                errors.Add(nameof(HitMaxScore), "must be positive");
            if (MaxScore < HitMaxScore)
                errors.Add(nameof(MaxScore), "must be at least HitMaxScore");
        }

        /// <summary>The tolerance and ceiling against the clock-sync cap; the score against <c>QualityCaps</c> minigame.</summary>
        public void Validate(ConfigSet set, RowErrors errors)
        {
            if (Kind != ForgeChallengeKind.TimingBar)
                return;
            if (set.TryGet(typeof(StartingValuesM1), out var values) && values.Rows.Count == 1)
            {
                int cap = ((StartingValuesM1)values.Rows[0]).LagCompensationCapMs;
                if (LatencyToleranceMs > cap)
                    errors.Add(nameof(LatencyToleranceMs), $"{LatencyToleranceMs} is above StartingValuesM1.lagCompensationCapMs ({cap})");
                if (MaxLagMs < cap)
                    errors.Add(nameof(MaxLagMs), $"{MaxLagMs} is below StartingValuesM1.lagCompensationCapMs ({cap})");
            }
            if (set.TryGet(typeof(QualityCaps), out var caps))
            {
                foreach (var row in caps.Rows)
                {
                    var cap = (QualityCaps)row;
                    if (QualityCaps.TryComponent(cap.Id, out var component) && component == QualityComponent.Minigame && MaxScore > cap.Cap)
                        errors.Add(nameof(MaxScore), $"{MaxScore} is above QualityCaps minigame ({cap.Cap})");
                }
            }
        }

        public static ForgeChallengeDef? ForJob(ConfigSet set, string jobId) => NetIds.Find<ForgeChallengeDef>(set, jobId, row => row.Id);
    }
}
