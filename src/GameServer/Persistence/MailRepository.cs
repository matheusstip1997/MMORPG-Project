using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Npgsql;
using Shared.Protocol;
using Shared.Util;

namespace GameServer.Persistence;

/// <summary>
/// Writes a system mail <b>inside a caller's open transaction</b> (spec 4.5 M4 mail, WP-M4-3): the <c>mail</c> row, its
/// <c>mail_items</c> and the <c>mail_ops</c> send row, keyed by the draft's operation key. The one SQL path of every send:
/// the character save that carries a <see cref="MailBatch"/> (<see cref="MailRepository.WriteAsync"/>), and the stand-alone
/// writers (the admin compensation, <c>DbMigrate --mail</c>, world jobs such as a listing's expiry), which open their own
/// transaction around the change that produced the attachments and call this in it. Never called from the tick thread; the
/// draft is checked by the caller (<c>Items/Mail/MailRules.Check</c>).
/// </summary>
public static class MailWriter
{
    /// <summary>The unique key of <c>mail.send_op_key</c>: one mail per operation key.</summary>
    public const string SendKeyConstraint = "mail_send_op_key";

    /// <summary>The primary key of <c>mail_ops</c>: a violation means the operation was applied.</summary>
    public const string OpKeyConstraint = "mail_ops_pkey";

    /// <summary>
    /// Writes <paramref name="draft"/> in <paramref name="transaction"/> and returns the new mail id. When the draft's operation
    /// key was sent before: with <paramref name="skipDuplicate"/> null and nothing written (an idempotent stand-alone retry);
    /// without it, a unique violation on <see cref="SendKeyConstraint"/> the caller rolls back (a save carrying a send twice).
    /// <paramref name="carrierId"/> is the character whose save carries the send (null for a stand-alone send).
    /// </summary>
    public static async Task<long?> SendAsync(DbConnection connection, DbTransaction transaction, MailDraft draft, long createdUtcMs,
        long expiresUtcMs, long? carrierId, Guid? saveId, bool skipDuplicate, CancellationToken ct)
    {
        string conflict = skipDuplicate ? "ON CONFLICT ON CONSTRAINT mail_send_op_key DO NOTHING" : "";
        var mailId = await connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition($"""
            INSERT INTO mail (recipient_id, kind, subject_id, subject_args, gold, state, created_at, expires_at, send_op_key,
                              carrier_id, source)
            VALUES (@ToCharacterId, @Kind, @SubjectId, @Args::jsonb, @Gold, 0, @CreatedAt, @ExpiresAt, @OpKey, @carrierId, @Source)
            {conflict}
            RETURNING mail_id
            """, new
        {
            draft.ToCharacterId, Kind = (short)draft.Kind, draft.SubjectId, Args = MailJson.Args(draft.Args), draft.Gold,
            CreatedAt = DbTime.ToUtc(createdUtcMs), ExpiresAt = DbTime.ToUtc(expiresUtcMs), draft.OpKey, carrierId, draft.Source,
        }, transaction, cancellationToken: ct));
        if (mailId == null)
            return null;
        var items = new List<MailItemRow>(draft.Items.Count);
        for (int i = 0; i < draft.Items.Count; i++)
        {
            var a = draft.Items[i];
            items.Add(new MailItemRow(i, a.DefId, a.Rarity, a.Count, a.InstanceId, a.Stolen, a.NonTradable, a.PropertiesJson));
        }
        if (items.Count > 0)
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO mail_items (mail_id, idx, def_id, rarity, count, instance_id, stolen, non_tradable, properties)
                VALUES (@mailId, @Idx, @DefId, @Rarity, @Count, @InstanceId, @Stolen, @NonTradable, @Properties::jsonb)
                """, items.Select(r => new
            {
                mailId, Idx = (short)r.Index, r.DefId, Rarity = (short)r.Rarity, r.Count, r.InstanceId, r.Stolen, r.NonTradable,
                Properties = r.PropertiesJson,
            }), transaction, cancellationToken: ct));
        }
        await InsertOpAsync(connection, transaction, draft.OpKey, MailOpKind.Send, mailId.Value, draft.ToCharacterId, draft.Gold,
            items, saveId, createdUtcMs, ct);
        return mailId;
    }

    /// <summary>Open mails of a character (the cap that gates new mail sources, <c>MailboxFull</c>), in a caller's transaction.</summary>
    public static Task<int> CountOpenAsync(DbConnection connection, DbTransaction? transaction, long characterId, CancellationToken ct) =>
        connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*)::int FROM mail WHERE recipient_id = @characterId AND state = 0", new { characterId }, transaction,
            cancellationToken: ct));

    internal static Task InsertOpAsync(DbConnection connection, DbTransaction transaction, string opKey, MailOpKind kind, long mailId,
        long characterId, long gold, IReadOnlyList<MailItemRow> items, Guid? saveId, long atUtcMs, CancellationToken ct) =>
        connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO mail_ops (op_key, kind, mail_id, character_id, gold, items, save_id, created_at)
            VALUES (@opKey, @Kind, @mailId, @characterId, @gold, @Items::jsonb, @saveId, @At)
            """, new { opKey, Kind = (short)kind, mailId, characterId, gold, Items = MailJson.Items(items), saveId, At = DbTime.ToUtc(atUtcMs) },
            transaction, cancellationToken: ct));
}

/// <summary>
/// The mail tables in the realm database (WP-M4-3, spec 4.5, 7.1): writes a <see cref="MailBatch"/> inside the character save
/// that carries it (<see cref="CharacterRepository.SaveAsync"/>), reads a login's open mails, and closes expired mails for the
/// expiry job (<see cref="MailWorker"/>). Only <c>MailService</c> produces batches. Never called from the tick thread.
/// </summary>
public sealed class MailRepository(NpgsqlDataSource realmDb, IClock clock) : IMailStore
{
    /// <summary>The open mails of a character, oldest first, at most <paramref name="maxRows"/>, and how many are open.</summary>
    public async Task<MailRecord> LoadOpenAsync(long characterId, int maxRows, CancellationToken ct = default)
    {
        await using var connection = await realmDb.OpenConnectionAsync(ct);
        int count = await MailWriter.CountOpenAsync(connection, null, characterId, ct);
        if (count == 0)
            return MailRecord.Empty;
        var mails = (await connection.QueryAsync<MailDbRow>(new CommandDefinition("""
            SELECT mail_id AS MailId, recipient_id AS RecipientId, kind AS Kind, subject_id AS SubjectId, subject_args::text AS Args,
                   gold AS Gold, created_at AS CreatedAt, expires_at AS ExpiresAt
            FROM mail WHERE recipient_id = @characterId AND state = 0 ORDER BY created_at, mail_id LIMIT @maxRows
            """, new { characterId, maxRows }, cancellationToken: ct))).ToList();
        var items = await ItemsOfAsync(connection, null, mails.Select(m => m.MailId).ToArray(), ct);
        return new MailRecord(mails.Select(m => m.ToRow(items)).ToList(), count);
    }

    /// <summary>One mail in any state (tests and admin tools); null when there is none.</summary>
    public async Task<(MailRow Mail, MailState State)?> GetAsync(long mailId, CancellationToken ct = default)
    {
        await using var connection = await realmDb.OpenConnectionAsync(ct);
        var mail = await connection.QuerySingleOrDefaultAsync<MailDbRow>(new CommandDefinition("""
            SELECT mail_id AS MailId, recipient_id AS RecipientId, kind AS Kind, subject_id AS SubjectId, subject_args::text AS Args,
                   gold AS Gold, created_at AS CreatedAt, expires_at AS ExpiresAt, state AS State
            FROM mail WHERE mail_id = @mailId
            """, new { mailId }, cancellationToken: ct));
        if (mail == null)
            return null;
        var items = await ItemsOfAsync(connection, null, new[] { mailId }, ct);
        return (mail.ToRow(items), (MailState)mail.State);
    }

    /// <summary>The mail operations of a mail, oldest first (tests and admin tools): kind, key, gold.</summary>
    public async Task<IReadOnlyList<(MailOpKind Kind, string OpKey, long Gold)>> OpsAsync(long mailId, CancellationToken ct = default)
    {
        await using var connection = await realmDb.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<(short Kind, string OpKey, long Gold)>(new CommandDefinition(
            "SELECT kind, op_key, gold FROM mail_ops WHERE mail_id = @mailId ORDER BY created_at, op_key", new { mailId },
            cancellationToken: ct));
        return rows.Select(r => ((MailOpKind)r.Kind, r.OpKey, r.Gold)).ToList();
    }

    /// <summary>Open mails of a character (the cap of an offline recipient).</summary>
    public async Task<int> CountOpenAsync(long characterId, CancellationToken ct = default)
    {
        await using var connection = await realmDb.OpenConnectionAsync(ct);
        return await MailWriter.CountOpenAsync(connection, null, characterId, ct);
    }

    /// <summary>
    /// The expiry job (spec 4.5: expired attachments are destroyed, a sink): closes up to <paramref name="max"/> open mails whose
    /// expiry passed at least <paramref name="graceMs"/> ago, in one transaction with a <c>mail_ops</c> expire row each (key
    /// <c>mailExpire:&lt;id&gt;</c>), and returns what they destroyed. Rows a concurrent run holds are skipped; a re-run finds
    /// nothing to do (idempotent). Time from <see cref="IClock"/> (rule 6).
    /// </summary>
    public async Task<IReadOnlyList<MailExpiredRow>> ExpireDueAsync(long graceMs, int max, CancellationToken ct = default)
    {
        long nowMs = clock.UtcNowMs;
        var due = DbTime.ToUtc(nowMs - graceMs);
        await using var connection = await realmDb.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var mails = (await connection.QueryAsync<MailDbRow>(new CommandDefinition("""
            SELECT mail_id AS MailId, recipient_id AS RecipientId, kind AS Kind, subject_id AS SubjectId, subject_args::text AS Args,
                   gold AS Gold, created_at AS CreatedAt, expires_at AS ExpiresAt
            FROM mail WHERE state = 0 AND expires_at <= @due ORDER BY expires_at, mail_id LIMIT @max FOR UPDATE SKIP LOCKED
            """, new { due, max }, transaction, cancellationToken: ct))).ToList();
        if (mails.Count == 0)
        {
            await transaction.RollbackAsync(ct);
            return Array.Empty<MailExpiredRow>();
        }
        var items = await ItemsOfAsync(connection, transaction, mails.Select(m => m.MailId).ToArray(), ct);
        var expired = new List<MailExpiredRow>(mails.Count);
        foreach (var mail in mails)
        {
            var row = mail.ToRow(items);
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE mail SET state = 2, closed_at = @now WHERE mail_id = @MailId AND state = 0",
                new { now = DbTime.ToUtc(nowMs), mail.MailId }, transaction, cancellationToken: ct));
            await MailWriter.InsertOpAsync(connection, transaction, ExpireKey(mail.MailId), MailOpKind.Expire, mail.MailId,
                mail.RecipientId, mail.Gold, row.Items, null, nowMs, ct);
            expired.Add(new MailExpiredRow(mail.MailId, mail.RecipientId, row.Kind, mail.Gold, row.Items));
        }
        await transaction.CommitAsync(ct);
        return expired;
    }

    /// <summary>The operation key of a mail's expiry (one per mail).</summary>
    public static string ExpireKey(long mailId) => "mailExpire:" + mailId;

    /// <summary>
    /// Writes <paramref name="batch"/> inside <paramref name="transaction"/> (after the inventory batch): the sends it carries,
    /// then its claims, each guarded by the values the world saw. A send key applied before throws a unique violation on
    /// <see cref="MailWriter.SendKeyConstraint"/>, a claim key on <see cref="MailWriter.OpKeyConstraint"/>; a mail changed under
    /// a claim throws <see cref="MailConflictException"/>. The caller rolls back.
    /// </summary>
    internal static async Task WriteAsync(DbConnection connection, DbTransaction transaction, long characterId, MailBatch batch,
        Guid saveId, CancellationToken ct)
    {
        foreach (var send in batch.Sends)
        {
            send.MailId = await MailWriter.SendAsync(connection, transaction, send.Draft, send.CreatedUtcMs, send.ExpiresUtcMs,
                characterId, saveId, skipDuplicate: false, ct) ?? 0;
        }
        foreach (var claim in batch.Claims)
        {
            if (claim.CharacterId != characterId)
                throw new InvalidOperationException($"a claim of {claim.CharacterId} in the save of {characterId}");
            var at = DbTime.ToUtc(claim.AtUtcMs);
            int updated = await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE mail SET gold = gold - @GoldTaken,
                                state = CASE WHEN @Emptied THEN 1 ELSE 0 END,
                                closed_at = CASE WHEN @Emptied THEN @at ELSE NULL END
                WHERE mail_id = @MailId AND recipient_id = @CharacterId AND state = 0 AND gold = @ExpectedGold
                """, new { claim.GoldTaken, claim.Emptied, at, claim.MailId, claim.CharacterId, claim.ExpectedGold }, transaction,
                cancellationToken: ct));
            if (updated != 1)
                throw new MailConflictException(claim.MailId);
            foreach (var take in claim.Items)
            {
                if (take.Taken <= 0)
                    continue;
                string sql = take.Taken == take.ExpectedCount
                    ? "DELETE FROM mail_items WHERE mail_id = @MailId AND idx = @Idx AND count = @ExpectedCount"
                    : "UPDATE mail_items SET count = count - @Taken WHERE mail_id = @MailId AND idx = @Idx AND count = @ExpectedCount";
                int changed = await connection.ExecuteAsync(new CommandDefinition(sql,
                    new { claim.MailId, Idx = (short)take.Index, take.ExpectedCount, take.Taken }, transaction, cancellationToken: ct));
                if (changed != 1)
                    throw new MailConflictException(claim.MailId);
            }
            await MailWriter.InsertOpAsync(connection, transaction, claim.OpKey, MailOpKind.Claim, claim.MailId, characterId,
                claim.GoldTaken, claim.Taken, saveId, claim.AtUtcMs, ct);
        }
    }

    private static async Task<ILookup<long, MailItemRow>> ItemsOfAsync(DbConnection connection, DbTransaction? transaction,
        long[] mailIds, CancellationToken ct)
    {
        var rows = await connection.QueryAsync<ItemDbRow>(new CommandDefinition("""
            SELECT mail_id AS MailId, idx AS Idx, def_id AS DefId, rarity AS Rarity, count AS Count, instance_id AS InstanceId,
                   stolen AS Stolen, non_tradable AS NonTradable, properties::text AS Properties
            FROM mail_items WHERE mail_id = ANY(@mailIds) ORDER BY mail_id, idx
            """, new { mailIds }, transaction, cancellationToken: ct));
        return rows.ToLookup(r => r.MailId, r => new MailItemRow(r.Idx, r.DefId, (byte)r.Rarity, r.Count, r.InstanceId, r.Stolen,
            r.NonTradable, r.Properties));
    }

    private sealed class MailDbRow
    {
        public long MailId { get; set; }
        public long RecipientId { get; set; }
        public short Kind { get; set; }
        public string SubjectId { get; set; } = "";
        public string Args { get; set; } = "[]";
        public long Gold { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime ExpiresAt { get; set; }
        public short State { get; set; }

        public MailRow ToRow(ILookup<long, MailItemRow> items) => new(MailId, RecipientId, (MailKind)Kind, SubjectId,
            MailJson.ParseArgs(Args), Gold, DbTime.ToUtcMs(CreatedAt), DbTime.ToUtcMs(ExpiresAt), items[MailId].ToList());
    }

    private sealed class ItemDbRow
    {
        public long MailId { get; set; }
        public short Idx { get; set; }
        public string DefId { get; set; } = "";
        public short Rarity { get; set; }
        public int Count { get; set; }
        public long InstanceId { get; set; }
        public bool Stolen { get; set; }
        public bool NonTradable { get; set; }
        public string? Properties { get; set; }
    }
}

/// <summary>The JSON columns of the mail tables (<c>subject_args</c>, <c>mail_ops.items</c>).</summary>
internal static class MailJson
{
    public static string Args(IReadOnlyList<MailArg> args) =>
        JsonSerializer.Serialize(args.Select(a => new ArgJson { N = a.Name, K = (byte)a.Kind, V = a.Value }));

    public static IReadOnlyList<MailArg> ParseArgs(string json) =>
        (JsonSerializer.Deserialize<List<ArgJson>>(json) ?? new List<ArgJson>())
        .Select(a => new MailArg(a.N, (MailArgKind)a.K, a.V)).ToList();

    public static string Items(IReadOnlyList<MailItemRow> items) =>
        JsonSerializer.Serialize(items.Select(i => new ItemJson { I = i.Index, D = i.DefId, R = i.Rarity, C = i.Count, N = i.InstanceId }));

    private sealed class ArgJson
    {
        [JsonPropertyName("n")] public string N { get; set; } = "";
        [JsonPropertyName("k")] public byte K { get; set; }
        [JsonPropertyName("v")] public long V { get; set; }
    }

    private sealed class ItemJson
    {
        [JsonPropertyName("i")] public int I { get; set; }
        [JsonPropertyName("d")] public string D { get; set; } = "";
        [JsonPropertyName("r")] public byte R { get; set; }
        [JsonPropertyName("c")] public int C { get; set; }
        [JsonPropertyName("n")] public long N { get; set; }
    }
}
