using System.Text.Json;
using Dapper;
using GameServer.Economy;
using Npgsql;

namespace GameServer.Persistence;

/// <summary>Faucet and sink totals of one UTC hour by reason name (camelCase, <see cref="EconomyReasonNames"/>).</summary>
public sealed record EconomyHourTotals(long HourStartMs, long GoldCreated, long GoldDestroyed,
    IReadOnlyDictionary<string, long> CreatedByReason, IReadOnlyDictionary<string, long> DestroyedByReason);

/// <summary>One reason's figures in a snapshot row.</summary>
public sealed record EconomyReasonFigures(long Created, long Destroyed, long ItemsDestroyed);

/// <summary>A <c>faucet_sink_snapshots</c> row as read back.</summary>
public sealed record EconomySnapshotRow(int WorldId, long TakenAtMs, long PeriodStartMs, long GoldCreated, long GoldDestroyed,
    long GoldOnline, int ActivePlayers, IReadOnlyDictionary<string, EconomyReasonFigures> ByReason);

/// <summary>Where the snapshot writer inserts rows (the repository; a test double in tests).</summary>
public interface IEconomySnapshotStore
{
    Task InsertSnapshotAsync(int worldId, EconomySnapshot snapshot, CancellationToken ct = default);
}

/// <summary>
/// The economy figures in the realm database (WP-M4-5; spec 7.1, 7.2 R-7.2-02, 7.4): the <c>faucet_sink_snapshots</c> rows the
/// worlds write after their demand control steps, and the faucet and sink totals per UTC hour and by reason from
/// <c>gold_ledger</c>, the authoritative record of every gold change (gold moved between owners is neither a faucet nor a
/// sink, R-4.5-02). Called from the snapshot worker and the admin panel, never from the tick thread (spec 1.6).
/// </summary>
public sealed class EconomyRepository(NpgsqlDataSource realmDb) : IEconomySnapshotStore
{
    public const long HourMs = 3_600_000;

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Inserts one snapshot row; a retry of a row already written (same world and time) changes nothing.</summary>
    public async Task InsertSnapshotAsync(int worldId, EconomySnapshot snapshot, CancellationToken ct = default)
    {
        var byReason = new SortedDictionary<string, EconomyReasonFigures>(StringComparer.Ordinal);
        foreach (var reason in snapshot.ByReason)
            byReason[EconomyReasonNames.Of(reason.Reason)] = new EconomyReasonFigures(reason.GoldCreated, reason.GoldDestroyed, reason.ItemsDestroyed);
        await using var connection = await realmDb.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO faucet_sink_snapshots (world_id, taken_at, period_start, gold_created, gold_destroyed, gold_online,
                active_players, by_reason)
            VALUES (@worldId, @takenAt, @periodStart, @GoldCreated, @GoldDestroyed, @GoldOnline, @ActivePlayers, @byReason::jsonb)
            ON CONFLICT ON CONSTRAINT faucet_sink_snapshots_world_time DO NOTHING
            """, new
        {
            worldId,
            takenAt = DbTime.ToUtc(snapshot.TakenAtMs),
            periodStart = DbTime.ToUtc(snapshot.PeriodStartMs),
            snapshot.GoldCreated,
            snapshot.GoldDestroyed,
            snapshot.GoldOnline,
            snapshot.ActivePlayers,
            byReason = JsonSerializer.Serialize(byReason, Json),
        }, cancellationToken: ct));
    }

    /// <summary>
    /// Gold created (faucet rows) and destroyed (sink rows) per UTC hour and by reason, for the ledger rows written in
    /// [<paramref name="fromMs"/>, <paramref name="toMs"/>); hours without a faucet or sink row are left out. A row's hour is
    /// its <c>created_at</c> rounded down to the hour, as the BotClient report rounds a telemetry record's time.
    /// </summary>
    public async Task<IReadOnlyList<EconomyHourTotals>> HourlyTotalsAsync(long fromMs, long toMs, CancellationToken ct = default)
    {
        await using var connection = await realmDb.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<HourRow>(new CommandDefinition("""
            SELECT (floor(extract(epoch FROM created_at) / 3600) * 3600000)::bigint AS HourStartMs,
                   flow AS Flow, reason AS Reason,
                   sum(CASE WHEN flow = @faucet THEN delta ELSE -delta END)::bigint AS Amount
            FROM gold_ledger
            WHERE created_at >= @from AND created_at < @to AND flow IN (@faucet, @sink)
            GROUP BY 1, 2, 3
            ORDER BY 1, 2, 3
            """, new
        {
            from = DbTime.ToUtc(fromMs), to = DbTime.ToUtc(toMs), faucet = (short)GoldFlow.Faucet, sink = (short)GoldFlow.Sink,
        }, cancellationToken: ct));

        var hours = new List<EconomyHourTotals>();
        foreach (var hour in rows.GroupBy(r => r.HourStartMs))
        {
            var created = new SortedDictionary<string, long>(StringComparer.Ordinal);
            var destroyed = new SortedDictionary<string, long>(StringComparer.Ordinal);
            foreach (var row in hour)
            {
                var into = row.Flow == (short)GoldFlow.Faucet ? created : destroyed;
                string name = EconomyReasonNames.OfLedger(row.Reason);
                into[name] = into.GetValueOrDefault(name) + row.Amount;
            }
            hours.Add(new EconomyHourTotals(hour.Key, created.Values.Sum(), destroyed.Values.Sum(), created, destroyed));
        }
        return hours;
    }

    /// <summary>The newest <paramref name="limit"/> snapshot rows taken in [<paramref name="fromMs"/>, <paramref name="toMs"/>), oldest first.</summary>
    public async Task<IReadOnlyList<EconomySnapshotRow>> SnapshotsAsync(long fromMs, long toMs, int limit, CancellationToken ct = default)
    {
        await using var connection = await realmDb.OpenConnectionAsync(ct);
        var rows = (await connection.QueryAsync<SnapshotRecord>(new CommandDefinition("""
            SELECT world_id AS WorldId, taken_at AS TakenAt, period_start AS PeriodStart, gold_created AS GoldCreated,
                   gold_destroyed AS GoldDestroyed, gold_online AS GoldOnline, active_players AS ActivePlayers,
                   by_reason::text AS ByReason
            FROM faucet_sink_snapshots
            WHERE taken_at >= @from AND taken_at < @to
            ORDER BY taken_at DESC, snapshot_id DESC
            LIMIT @limit
            """, new { from = DbTime.ToUtc(fromMs), to = DbTime.ToUtc(toMs), limit }, cancellationToken: ct))).ToList();
        rows.Reverse();
        return rows.Select(r => new EconomySnapshotRow(r.WorldId, DbTime.ToUtcMs(r.TakenAt), DbTime.ToUtcMs(r.PeriodStart),
            r.GoldCreated, r.GoldDestroyed, r.GoldOnline, r.ActivePlayers, ReadReasons(r.ByReason))).ToList();
    }

    private static IReadOnlyDictionary<string, EconomyReasonFigures> ReadReasons(string json) =>
        new SortedDictionary<string, EconomyReasonFigures>(
            JsonSerializer.Deserialize<Dictionary<string, EconomyReasonFigures>>(json, Json) ?? new(), StringComparer.Ordinal);

    private sealed class HourRow
    {
        public long HourStartMs { get; init; }
        public short Flow { get; init; }
        public string Reason { get; init; } = "";
        public long Amount { get; init; }
    }

    private sealed class SnapshotRecord
    {
        public int WorldId { get; init; }
        public DateTime TakenAt { get; init; }
        public DateTime PeriodStart { get; init; }
        public long GoldCreated { get; init; }
        public long GoldDestroyed { get; init; }
        public long GoldOnline { get; init; }
        public int ActivePlayers { get; init; }
        public string ByReason { get; init; } = "{}";
    }
}
