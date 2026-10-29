using System.Data.Common;
using Dapper;
using Npgsql;

namespace GameServer.Persistence;

/// <summary>
/// Gold in the realm database (spec 4.2, 7.1; WP-M2-4): the balance on <c>characters.gold</c> and the <c>gold_ledger</c>.
/// Written only inside the transaction of the character save that carries an <see cref="InventoryBatch"/> with gold
/// (<see cref="InventoryRepository.WriteAsync"/>), so only <c>InventoryService</c> changes gold (architecture rule 4).
/// Called from persistence workers and tests, never from the tick thread (spec 1.6).
/// </summary>
public sealed class GoldRepository(NpgsqlDataSource realmDb)
{
    /// <summary>The ledger rows of a character, in write order (tests and admin tools).</summary>
    public async Task<IReadOnlyList<GoldEntry>> LedgerAsync(long characterId, CancellationToken ct = default)
    {
        await using var connection = await realmDb.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<LedgerRow>(new CommandDefinition("""
            SELECT op_key AS OpKey, delta AS Delta, balance_after AS BalanceAfter, flow AS Flow, reason AS Reason, source AS Source
            FROM gold_ledger WHERE character_id = @characterId ORDER BY ledger_id
            """, new { characterId }, cancellationToken: ct));
        return rows.Select(r => new GoldEntry(r.OpKey, r.Delta, r.BalanceAfter, (GoldFlow)r.Flow, r.Reason, r.Source)).ToList();
    }

    /// <summary>The committed balance (tests and admin tools; the game reads it with the character row).</summary>
    public async Task<long> BalanceAsync(long characterId, CancellationToken ct = default)
    {
        await using var connection = await realmDb.OpenConnectionAsync(ct);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT gold FROM characters WHERE character_id = @characterId", new { characterId }, cancellationToken: ct));
    }

    /// <summary>
    /// Writes <paramref name="batch"/> inside <paramref name="transaction"/>, after the batch's operation keys: the final
    /// balance, then the ledger rows.
    /// </summary>
    internal static async Task WriteAsync(DbConnection connection, DbTransaction transaction, long characterId,
        GoldBatch batch, Guid saveId, DateTime now, CancellationToken ct)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE characters SET gold = @GoldAfter WHERE character_id = @characterId",
            new { batch.GoldAfter, characterId }, transaction, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO gold_ledger (op_key, character_id, delta, balance_after, flow, reason, source, save_id, created_at)
            VALUES (@OpKey, @characterId, @Delta, @BalanceAfter, @Flow, @Reason, @Source, @saveId, @now)
            """, batch.Entries.Select(e => new
            {
                e.OpKey, characterId, e.Delta, e.BalanceAfter, Flow = (short)e.Flow, e.Reason, e.Source, saveId, now,
            }), transaction, cancellationToken: ct));
    }

    private sealed class LedgerRow
    {
        public string OpKey { get; init; } = "";
        public long Delta { get; init; }
        public long BalanceAfter { get; init; }
        public short Flow { get; init; }
        public string Reason { get; init; } = "";
        public string Source { get; init; } = "";
    }
}
