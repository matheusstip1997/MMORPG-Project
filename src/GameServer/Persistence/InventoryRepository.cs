using System.Data.Common;
using Dapper;
using Npgsql;

namespace GameServer.Persistence;

/// <summary>
/// Items in the realm database (backlog G, spec 4.1, 7.1). Reads a character's stacks for a login; writes an
/// <see cref="InventoryBatch"/> inside the transaction of the character save that carries it
/// (<see cref="CharacterRepository.SaveAsync"/>), so items, the idempotency keys and the audit rows commit together with
/// the version-checked character row (architecture rules 4 and 5). Only <c>InventoryService</c> produces batches.
/// Called from persistence workers and session tasks, never from the tick thread (spec 1.6).
/// </summary>
public sealed class InventoryRepository(NpgsqlDataSource realmDb)
{
    /// <summary>Primary key of <c>inventory_ops</c>: a violation means an operation was already applied.</summary>
    internal const string OpKeyConstraint = "inventory_ops_pkey";

    /// <summary>The character's stacks, in container and slot order.</summary>
    public async Task<IReadOnlyList<ItemStackRow>> LoadAsync(long characterId, CancellationToken ct = default)
    {
        await using var connection = await realmDb.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<StackRow>(new CommandDefinition("""
            SELECT container AS Container, slot AS Slot, def_id AS DefId, rarity AS Rarity, stolen AS Stolen, count AS Count
            FROM item_stacks WHERE character_id = @characterId ORDER BY container, slot
            """, new { characterId }, cancellationToken: ct));
        return rows.Select(r => new ItemStackRow(r.Container, r.Slot, r.DefId, r.Rarity, r.Stolen, r.Count)).ToList();
    }

    /// <summary>The operation keys applied for a character, oldest first (tests and admin tools).</summary>
    public async Task<IReadOnlyList<string>> OpKeysAsync(long characterId, CancellationToken ct = default)
    {
        await using var connection = await realmDb.OpenConnectionAsync(ct);
        var keys = await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT op_key FROM inventory_ops WHERE character_id = @characterId ORDER BY created_at, op_key",
            new { characterId }, cancellationToken: ct));
        return keys.ToList();
    }

    /// <summary>The audit rows of a character, in write order (tests and admin tools).</summary>
    public async Task<IReadOnlyList<AuditEntry>> AuditAsync(long characterId, CancellationToken ct = default)
    {
        await using var connection = await realmDb.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<AuditRow>(new CommandDefinition("""
            SELECT op_key AS OpKey, seq AS Seq, kind AS Kind, container AS Container, slot AS Slot, def_id AS DefId,
                rarity AS Rarity, stolen AS Stolen, count_delta AS CountDelta, count_after AS CountAfter
            FROM audit_log WHERE character_id = @characterId ORDER BY audit_id
            """, new { characterId }, cancellationToken: ct));
        return rows.Select(r => new AuditEntry(r.OpKey, r.Seq, (AuditKind)r.Kind, r.Container, r.Slot, r.DefId, r.Rarity,
            r.Stolen, r.CountDelta, r.CountAfter)).ToList();
    }

    /// <summary>
    /// Writes <paramref name="batch"/> inside <paramref name="transaction"/>: the operation keys first (a key applied
    /// before throws a unique violation on <see cref="OpKeyConstraint"/> and the caller rolls back), then the final state
    /// of each touched slot, then the audit rows.
    /// </summary>
    internal static async Task WriteAsync(DbConnection connection, DbTransaction transaction, long characterId,
        InventoryBatch batch, Guid saveId, DateTime now, CancellationToken ct)
    {
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO inventory_ops (op_key, character_id, reason, source, save_id, created_at)
            VALUES (@OpKey, @characterId, @Reason, @Source, @saveId, @now)
            """, batch.Ops.Select(o => new { o.OpKey, characterId, o.Reason, o.Source, saveId, now }),
            transaction, cancellationToken: ct));

        // Final slot states: delete every touched slot, then insert the occupied ones, so moves and swaps never trip
        // the slot key half way.
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM item_stacks WHERE character_id = @characterId AND container = @Container AND slot = @Slot",
            batch.Slots.Select(s => new { characterId, Container = (short)s.Container, Slot = (short)s.Slot }),
            transaction, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO item_stacks (character_id, container, slot, def_id, rarity, stolen, count, created_at, updated_at)
            VALUES (@characterId, @Container, @Slot, @DefId, @Rarity, @Stolen, @Count, @now, @now)
            """, batch.Slots.Where(s => !s.IsEmpty).Select(s => new
            {
                characterId, Container = (short)s.Container, Slot = (short)s.Slot, s.DefId, Rarity = (short)s.Rarity,
                s.Stolen, s.Count, now,
            }), transaction, cancellationToken: ct));

        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO audit_log (op_key, seq, character_id, kind, container, slot, def_id, rarity, stolen, count_delta,
                count_after, save_id, created_at)
            VALUES (@OpKey, @Seq, @characterId, @Kind, @Container, @Slot, @DefId, @Rarity, @Stolen, @CountDelta,
                @CountAfter, @saveId, @now)
            """, batch.Audit.Select(a => new
            {
                a.OpKey, Seq = (short)a.Seq, characterId, Kind = (short)a.Kind, Container = (short)a.Container,
                Slot = (short)a.Slot, a.DefId, Rarity = (short)a.Rarity, a.Stolen, a.CountDelta, a.CountAfter, saveId, now,
            }), transaction, cancellationToken: ct));
    }

    private sealed class StackRow
    {
        public short Container { get; init; }
        public short Slot { get; init; }
        public string DefId { get; init; } = "";
        public short Rarity { get; init; }
        public bool Stolen { get; init; }
        public int Count { get; init; }
    }

    private sealed class AuditRow
    {
        public string OpKey { get; init; } = "";
        public short Seq { get; init; }
        public short Kind { get; init; }
        public short Container { get; init; }
        public short Slot { get; init; }
        public string DefId { get; init; } = "";
        public short Rarity { get; init; }
        public bool Stolen { get; init; }
        public int CountDelta { get; init; }
        public int CountAfter { get; init; }
    }
}
