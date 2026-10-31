using System.Data.Common;
using Dapper;
using Npgsql;

namespace GameServer.Persistence;

/// <summary>
/// Forge operations in the realm database (WP-M3-6, spec 5.3, 7.1): writes a <see cref="CraftBatch"/> inside the transaction of
/// the character save that carries it (<see cref="CharacterRepository.SaveAsync"/>), so an operation, its consumed inputs and
/// its output commit together (rule 5); reads a login's recent conversions for the daily cap. Only <c>ForgeService</c>
/// produces batches. Never called from the tick thread.
/// </summary>
public sealed class CraftRepository(NpgsqlDataSource realmDb)
{
    /// <summary>The primary key of <c>craft_ops</c>: a violation means the operation was applied.</summary>
    internal const string OpKeyConstraint = "craft_ops_pkey";

    /// <summary>The character's conversions of the last two days (the daily cap, R-5.2-04).</summary>
    public async Task<CraftRecord> LoadAsync(long characterId, CancellationToken ct = default)
    {
        await using var connection = await realmDb.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<ConversionRow>(new CommandDefinition("""
            SELECT tier AS Tier, created_at AS CreatedAt FROM craft_ops
            WHERE character_id = @characterId AND kind = @kind AND created_at >= now() - interval '2 days'
            ORDER BY created_at
            """, new { characterId, kind = (short)CraftOpKind.Convert }, cancellationToken: ct));
        return new CraftRecord(rows.Select(r => new CraftConversion(r.Tier + 1, DbTime.ToUtcMs(r.CreatedAt))).ToList());
    }

    /// <summary>The forge operations of a character, oldest first (tests and admin tools).</summary>
    public async Task<IReadOnlyList<CraftOpRecord>> OpsAsync(long characterId, CancellationToken ct = default)
    {
        await using var connection = await realmDb.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<OpRow>(new CommandDefinition("""
            SELECT op_key AS OpKey, kind AS Kind, def_id AS DefId, tier AS Tier, inputs AS Inputs, output_item_id AS OutputItemId,
                   output_count AS OutputCount, output_instance_id AS OutputInstanceId, quality AS Quality, rarity AS Rarity,
                   success AS Success, inputs_kept AS InputsKept, seed AS Seed, recipient_id AS RecipientId, created_at AS CreatedAt
            FROM craft_ops WHERE character_id = @characterId ORDER BY created_at, op_key
            """, new { characterId }, cancellationToken: ct));
        return rows.Select(r => new CraftOpRecord(r.OpKey, (CraftOpKind)r.Kind, r.DefId, r.Tier, r.Inputs, r.OutputItemId,
            r.OutputCount, r.OutputInstanceId, r.Quality, r.Rarity, r.Success, unchecked((ulong)r.Seed), r.RecipientId,
            DbTime.ToUtcMs(r.CreatedAt), r.InputsKept)).ToList();
    }

    /// <summary>
    /// Writes <paramref name="batch"/> inside <paramref name="transaction"/>. A key applied before throws a unique violation on
    /// <see cref="OpKeyConstraint"/>, and the caller rolls back.
    /// </summary>
    internal static async Task WriteAsync(DbConnection connection, DbTransaction transaction, long characterId, CraftBatch batch,
        Guid saveId, CancellationToken ct)
    {
        if (batch.Ops.Count == 0)
            return;
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO craft_ops (op_key, character_id, kind, def_id, tier, inputs, output_item_id, output_count, output_instance_id,
                                   quality, rarity, success, inputs_kept, seed, recipient_id, save_id, created_at)
            VALUES (@OpKey, @characterId, @Kind, @DefId, @Tier, @Inputs, @OutputItemId, @OutputCount, @OutputInstanceId,
                    @Quality, @Rarity, @Success, @InputsKept, @Seed, @RecipientId, @saveId, @CreatedAt)
            """, batch.Ops.Select(o => new
        {
            o.OpKey, characterId, Kind = (short)o.Kind, o.DefId, Tier = (short)o.Tier, Inputs = o.Inputs.ToArray(), o.OutputItemId,
            o.OutputCount, o.OutputInstanceId, o.Quality, Rarity = (short)o.Rarity, o.Success, o.InputsKept, Seed = unchecked((long)o.Seed),
            o.RecipientId, saveId, CreatedAt = DbTime.ToUtc(o.AtUtcMs),
        }), transaction, cancellationToken: ct));
    }

    private sealed class ConversionRow
    {
        public short Tier { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    private sealed class OpRow
    {
        public string OpKey { get; set; } = "";
        public short Kind { get; set; }
        public string DefId { get; set; } = "";
        public short Tier { get; set; }
        public string[] Inputs { get; set; } = Array.Empty<string>();
        public string OutputItemId { get; set; } = "";
        public int OutputCount { get; set; }
        public long OutputInstanceId { get; set; }
        public int Quality { get; set; }
        public short Rarity { get; set; }
        public bool Success { get; set; }
        public bool InputsKept { get; set; }
        public long Seed { get; set; }
        public long RecipientId { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
