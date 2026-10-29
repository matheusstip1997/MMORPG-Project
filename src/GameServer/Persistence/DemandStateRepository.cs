using Dapper;
using GameServer.Economy;
using GameServer.Events;
using Npgsql;
using Shared.Util;

namespace GameServer.Persistence;

/// <summary>
/// The demand loop's state in the realm database (<c>demand_state</c>, R-4.2-03 "state persisted periodically";
/// WP-M2-4): read once when a world starts, written by <see cref="DemandStateWorker"/> every
/// <c>DemandSettings.persistIntervalMs</c> (volatile class, R-7.1-01). Never called from the tick thread (spec 1.6).
/// </summary>
public sealed class DemandStateRepository(NpgsqlDataSource realmDb, IClock clock)
{
    public async Task<IReadOnlyList<DemandStateRow>> LoadAsync(int worldId, CancellationToken ct = default)
    {
        await using var connection = await realmDb.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<Row>(new CommandDefinition("""
            SELECT key_kind AS KeyKind, def_id AS DefId, region_id AS RegionId, decayed_milli AS DecayedMilli,
                spawn_percent AS SpawnPercent, reward_percent AS RewardPercent
            FROM demand_state WHERE world_id = @worldId ORDER BY key_kind, def_id, region_id
            """, new { worldId }, cancellationToken: ct));
        return rows.Select(r => new DemandStateRow((DemandKeyKind)r.KeyKind, r.DefId, r.RegionId, r.DecayedMilli,
            r.SpawnPercent, r.RewardPercent)).ToList();
    }

    /// <summary>Upserts every row in one transaction (rows the world no longer has stay as they were).</summary>
    public async Task SaveAsync(int worldId, IReadOnlyList<DemandStateRow> rows, CancellationToken ct = default)
    {
        await using var connection = await realmDb.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var now = DbTime.ToUtc(clock.UtcNowMs);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO demand_state (world_id, key_kind, def_id, region_id, decayed_milli, spawn_percent, reward_percent, updated_at)
            VALUES (@worldId, @KeyKind, @DefId, @RegionId, @DecayedMilli, @SpawnPercent, @RewardPercent, @now)
            ON CONFLICT (world_id, key_kind, def_id, region_id) DO UPDATE SET decayed_milli = EXCLUDED.decayed_milli,
                spawn_percent = EXCLUDED.spawn_percent, reward_percent = EXCLUDED.reward_percent, updated_at = EXCLUDED.updated_at
            """, rows.Select(r => new
            {
                worldId, KeyKind = (short)r.Kind, r.DefId, r.RegionId, r.DecayedMilli, r.SpawnPercent, r.RewardPercent, now,
            }), transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
    }

    private sealed class Row
    {
        public short KeyKind { get; init; }
        public string DefId { get; init; } = "";
        public string RegionId { get; init; } = "";
        public long DecayedMilli { get; init; }
        public int SpawnPercent { get; init; }
        public int RewardPercent { get; init; }
    }
}
