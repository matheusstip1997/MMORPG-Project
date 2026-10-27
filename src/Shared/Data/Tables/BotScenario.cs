using System;
using System.Linq;

namespace Shared.Data.Tables
{
    /// <summary>
    /// One bot load-test run (backlog J, spec 11.5 load-test plan): how many bots of which classes, for how long, over which
    /// link, and the pass criteria the BotClient's <c>loadtest</c> command checks. The tick budget is one tick period
    /// (<c>StartingValuesM1.tickRateHz</c>); spec 11.5 proposes p99 below half of it, the M1 exit criterion (12.4) within
    /// it with 50 bots. Tools only; not a game rule.
    /// </summary>
    [Table("BotScenario", 1)]
    public sealed class BotScenario : IValidatableRow, ISetValidatableRow
    {
        /// <summary>For example <c>ci-smoke</c>.</summary>
        [TableId] public string Id { get; set; } = string.Empty;

        [Range(1, 500)] public int Bots { get; set; }

        /// <summary>How long the bots play once the last one entered the world.</summary>
        [Range(10_000, 86_400_000)] public int DurationMs { get; set; }

        /// <summary>Bots that start logging in per second.</summary>
        [Range(1, 50)] public int RampPerSecond { get; set; }

        /// <summary>Every bot must be in the world this long after the first login started, or the run fails.</summary>
        [Range(5_000, 600_000)] public int EnterTimeoutMs { get; set; }

        /// <summary>Character classes, round robin by bot index (each must be an allowed initial class).</summary>
        [Ref(typeof(ClassDef))] public string[] Classes { get; set; } = Array.Empty<string>();

        [Ref(typeof(BotBehaviour))] public string BehaviourId { get; set; } = string.Empty;

        [Ref(typeof(BotLinkProfile))] public string LinkId { get; set; } = string.Empty;

        /// <summary>Pass: tick time p99 at most this percent of the tick period.</summary>
        [Range(1, 1_000)] public int TickP99MaxPercentOfPeriod { get; set; }

        /// <summary>
        /// Pass: at least this percent of the bots had an attack of theirs run on the server with a living monster in reach.
        /// </summary>
        [Range(0, 100)] public int MinInCombatPercent { get; set; }

        /// <summary>Pass: at most this many bots lost their connection before the end of the run.</summary>
        [Range(0, 500)] public int MaxDisconnects { get; set; }

        /// <summary>
        /// Pass (the M2 exit run, spec 12.2): every bot reaches this level within <see cref="DurationMs"/>; the run ends
        /// early once they all have. 0: no level criterion.
        /// </summary>
        [Optional, Range(0, 200)] public int TargetLevel { get; set; }

        /// <summary>How often the load test samples each bot's level for the level-over-time report.</summary>
        [Optional, Range(1_000, 3_600_000)] public int LevelSampleMs { get; set; } = 60_000;

        public void Validate(RowErrors errors)
        {
            if (Classes.Length == 0)
                errors.Add(nameof(Classes), "needs at least one class");
        }

        public void Validate(ConfigSet set, RowErrors errors)
        {
            // The server takes at most maxPeers players and connectRequestsPerSecond logins (R-2.5-03).
            if (set.TryGet(typeof(NetSecurity), out var security) && security.Rows.Count == 1)
            {
                var limits = (NetSecurity)security.Rows[0];
                if (Bots > limits.MaxPeers)
                    errors.Add(nameof(Bots), $"{Bots} bots, but NetSecurity.maxPeers is {limits.MaxPeers}");
                if (RampPerSecond > limits.ConnectRequestsPerSecond)
                    errors.Add(nameof(RampPerSecond), $"above NetSecurity.connectRequestsPerSecond ({limits.ConnectRequestsPerSecond})");
            }
            if (set.TryGet(typeof(CharacterCreationConfig), out var creation) && creation.Rows.Count == 1)
            {
                var allowed = ((CharacterCreationConfig)creation.Rows[0]).AllowedInitialClasses;
                foreach (var classId in Classes.Where(c => !allowed.Contains(c)))
                    errors.Add(nameof(Classes), $"'{classId}' is not in CharacterCreationConfig.allowedInitialClasses");
            }
        }
    }
}
