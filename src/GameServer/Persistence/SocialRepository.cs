using System.Data.Common;
using Dapper;
using Npgsql;
using Shared.Protocol;

namespace GameServer.Persistence;

/// <summary>
/// Friends and the block list in the realm database (spec 6.5 "M4 social", 7.1; WP-M4-16): the <c>friends</c> and <c>blocks</c>
/// rows of migration V0023. Read at login off the tick (<see cref="LoadAsync"/>); written inside a character save
/// (<see cref="WriteAsync"/>). Every write is idempotent by itself (an insert that keeps an existing row, a delete of what may be
/// gone), so a save retried after an unknown outcome writes the same end state. Only <c>Social/SocialService</c> produces them.
/// </summary>
public sealed class SocialRepository(NpgsqlDataSource realmDb)
{
    /// <summary>The character's friends (the other side's id and name as this character saw it) and its block list, in id order.</summary>
    public async Task<SocialRecord> LoadAsync(long characterId, CancellationToken ct = default)
    {
        await using var connection = await realmDb.OpenConnectionAsync(ct);
        var friends = (await connection.QueryAsync<FriendDb>(new CommandDefinition("""
            SELECT CASE WHEN character_lo = @characterId THEN character_hi ELSE character_lo END AS OtherId,
                   CASE WHEN character_lo = @characterId THEN name_hi ELSE name_lo END AS Name
            FROM friends
            WHERE character_lo = @characterId OR character_hi = @characterId
            ORDER BY 1
            """, new { characterId }, cancellationToken: ct))).Select(r => new FriendRow(r.OtherId, r.Name)).ToList();
        var blocks = (await connection.QueryAsync<BlockDb>(new CommandDefinition("""
            SELECT target_id AS TargetId, kind AS Kind, target_name AS Name
            FROM blocks
            WHERE character_id = @characterId
            ORDER BY target_id
            """, new { characterId }, cancellationToken: ct)))
            .Select(r => new BlockRow(r.TargetId, (SocialBlockKind)r.Kind, r.Name)).ToList();
        return new SocialRecord(friends, blocks);
    }

    /// <summary>Writes <paramref name="batch"/> inside the character save's transaction, in the order the operations were made.</summary>
    internal static async Task WriteAsync(DbConnection connection, DbTransaction transaction, SocialBatch batch, DateTime now,
        CancellationToken ct)
    {
        foreach (var op in batch.Ops)
        {
            long lo = Math.Min(op.CharacterId, op.OtherId), hi = Math.Max(op.CharacterId, op.OtherId);
            switch (op.Kind)
            {
                case SocialOpKind.FriendAdd:
                {
                    // name_lo is lo's name as hi saw it: the own name when this character is lo, else the other's.
                    bool ownIsLo = op.CharacterId == lo;
                    await connection.ExecuteAsync(new CommandDefinition("""
                        INSERT INTO friends (character_lo, character_hi, name_lo, name_hi, created_at)
                        VALUES (@lo, @hi, @nameLo, @nameHi, @now)
                        ON CONFLICT (character_lo, character_hi) DO NOTHING
                        """, new { lo, hi, nameLo = ownIsLo ? op.OwnName : op.OtherName, nameHi = ownIsLo ? op.OtherName : op.OwnName, now },
                        transaction, cancellationToken: ct));
                    break;
                }
                case SocialOpKind.FriendRemove:
                    await connection.ExecuteAsync(new CommandDefinition(
                        "DELETE FROM friends WHERE character_lo = @lo AND character_hi = @hi", new { lo, hi }, transaction,
                        cancellationToken: ct));
                    break;
                case SocialOpKind.BlockSet:
                    await connection.ExecuteAsync(new CommandDefinition("""
                        INSERT INTO blocks (character_id, target_id, kind, target_name, created_at)
                        VALUES (@CharacterId, @OtherId, @kind, @OtherName, @now)
                        ON CONFLICT (character_id, target_id) DO UPDATE SET kind = EXCLUDED.kind, target_name = EXCLUDED.target_name
                        """, new { op.CharacterId, op.OtherId, kind = (short)op.BlockKind, op.OtherName, now }, transaction,
                        cancellationToken: ct));
                    break;
                case SocialOpKind.BlockRemove:
                    await connection.ExecuteAsync(new CommandDefinition(
                        "DELETE FROM blocks WHERE character_id = @CharacterId AND target_id = @OtherId", new { op.CharacterId, op.OtherId },
                        transaction, cancellationToken: ct));
                    break;
                default:
                    throw new InvalidOperationException($"unknown social operation {op.Kind}");
            }
        }
    }

    private sealed class FriendDb
    {
        public long OtherId { get; init; }
        public string Name { get; init; } = "";
    }

    private sealed class BlockDb
    {
        public long TargetId { get; init; }
        public short Kind { get; init; }
        public string Name { get; init; } = "";
    }
}
