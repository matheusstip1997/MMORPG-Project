namespace GameServer.Persistence;

/// <summary>Converts between <c>IClock</c> milliseconds and <c>timestamptz</c> values (always UTC, rule 6).</summary>
internal static class DbTime
{
    public static DateTime ToUtc(long utcMs) => DateTime.UnixEpoch.AddMilliseconds(utcMs);

    public static long ToUtcMs(DateTime value) => (long)(value.ToUniversalTime() - DateTime.UnixEpoch).TotalMilliseconds;
}
