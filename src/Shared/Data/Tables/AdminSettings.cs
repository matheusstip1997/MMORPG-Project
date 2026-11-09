namespace Shared.Data.Tables
{
    /// <summary>
    /// Admin panel v0 and the game server's admin command worker (spec 7.4 implementation, backlog J). Server and tools
    /// only; the client never reads it. Values in data/tables/AdminSettings.json.
    /// </summary>
    [Table("AdminSettings", 1)]
    [Singleton]
    public sealed class AdminSettings : IValidatableRow
    {
        /// <summary>How often a game server looks for admin commands addressed to it (kick latency, database load).</summary>
        [Range(50, 10_000)] public int CommandPollMs { get; set; }

        /// <summary>A command no game server finished within this time is closed as <c>Expired</c> (crashed or stopped server).</summary>
        [Range(1_000, 600_000)] public int CommandExpiryMs { get; set; }

        /// <summary>The most commands one poll claims.</summary>
        [Range(1, 1_000)] public int MaxClaimPerPoll { get; set; }

        /// <summary>The most characters one search returns.</summary>
        [Range(1, 1_000)] public int SearchMaxResults { get; set; }

        /// <summary>The most admin actions shown for a character or for the realm.</summary>
        [Range(1, 1_000)] public int ActionHistoryLimit { get; set; }

        /// <summary>The most server log lines one request returns.</summary>
        [Range(1, 10_000)] public int LogTailLines { get; set; }

        /// <summary>How far back from its end a log file is read, in bytes.</summary>
        [Range(1_024, 64 * 1_024 * 1_024)] public int LogTailMaxBytes { get; set; }

        /// <summary>How far back telemetry files are read, in bytes over the newest files.</summary>
        [Range(1_024, 256 * 1_024 * 1_024)] public int TelemetryTailMaxBytes { get; set; }

        /// <summary>The most telemetry records one request returns.</summary>
        [Range(1, 10_000)] public int TelemetryMaxRecords { get; set; }

        /// <summary>How long an admin sign-in lasts.</summary>
        [Range(60_000, 86_400_000)] public int SessionLifetimeMs { get; set; }

        /// <summary>The longest admin name, search text or kick note, in characters.</summary>
        [Range(8, 1_000)] public int MaxDetailChars { get; set; }

        /// <summary>The economy view's window when the request names none, in UTC hours ending with the current one (WP-M4-5).</summary>
        [Optional] [Range(1, 8_760)] public int EconomyDefaultHours { get; set; } = 24;

        /// <summary>The widest window the economy view serves, in hours (WP-M4-5).</summary>
        [Optional] [Range(1, 8_760)] public int EconomyMaxHours { get; set; } = 168;

        /// <summary>The most snapshot rows one economy view returns, the newest (WP-M4-5).</summary>
        [Optional] [Range(1, 10_000)] public int EconomySnapshotMaxRows { get; set; } = 500;

        public void Validate(RowErrors errors)
        {
            // A command must get several polls before it can expire, or a busy server would see its kicks expire.
            if (CommandExpiryMs < CommandPollMs * 4L)
                errors.Add(nameof(CommandExpiryMs), "must be at least 4 times CommandPollMs");
            if (EconomyDefaultHours > EconomyMaxHours)
                errors.Add(nameof(EconomyDefaultHours), "must not be above EconomyMaxHours");
        }
    }
}
