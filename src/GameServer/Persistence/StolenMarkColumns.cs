namespace GameServer.Persistence;

/// <summary>
/// The stolen mark's columns (<c>stolen_from</c>, <c>stolen_at</c>; realm V0021, WP-M4-9, R-4.4-01) on <c>item_stacks</c>,
/// <c>audit_log</c>, <c>mail_items</c> and <c>loot_bag_items</c>: NULL in the database for "none", 0 in memory.
/// </summary>
internal static class StolenMarkColumns
{
    public static long? From(long stolenFrom) => stolenFrom == 0 ? null : stolenFrom;

    public static DateTime? At(long stolenAtMs) => stolenAtMs == 0 ? null : DbTime.ToUtc(stolenAtMs);

    public static long ToMs(DateTime? stolenAt) => stolenAt.HasValue ? DbTime.ToUtcMs(stolenAt.Value) : 0;
}
