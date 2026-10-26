using Dapper;
using GameServer.Persistence;
using Npgsql;
using Shared.Util;

namespace GameServer.Admin;

/// <summary>
/// The admin action log in the realm database (<c>admin_actions</c>, realm migration V0003; spec 7.4). The admin panel
/// records every action here; a command (kick) is also the request the game server holding the character executes.
/// <para>
/// Atomic and idempotent (rule 5): a row is written in one statement keyed by its request id, so a double submit or a
/// retry after an unknown outcome finds the row instead of adding one; a claim takes each pending command once
/// (<c>FOR UPDATE SKIP LOCKED</c>), and a completion applies once. Used by the panel and by
/// <see cref="AdminCommandWorker"/>, never from the world thread.
/// </para>
/// </summary>
public sealed class AdminActionRepository(NpgsqlDataSource realmDb, int realmId, IClock clock)
{
    private const string Columns = """
        action_id AS ActionId, request_id AS RequestId, realm_id AS RealmId, admin_name AS AdminName, action AS Action,
        target_character_id AS TargetCharacterId, target_account_id AS TargetAccountId, target_server AS TargetServer,
        detail AS Detail, state AS State, outcome AS Outcome, requested_at AS RequestedAt, claimed_at AS ClaimedAt,
        claimed_by AS ClaimedBy, completed_at AS CompletedAt
        """;

    public int RealmId => realmId;

    /// <summary>
    /// Records an action. A command starts <see cref="AdminActionState.Pending"/> (<paramref name="outcome"/> null); a
    /// read or a refused command is recorded finished with its outcome. A row with the same request id is returned as it
    /// is: <see cref="AdminSubmitStatus.AlreadyRecorded"/> when it names the same action, target and admin, otherwise
    /// <see cref="AdminSubmitStatus.Conflict"/>.
    /// </summary>
    public async Task<(AdminSubmitStatus Status, AdminActionRecord Record)> SubmitAsync(AdminActionRequest request,
        AdminOutcome? outcome, CancellationToken ct = default)
    {
        var sql = $"""
            INSERT INTO admin_actions (request_id, realm_id, admin_name, action, target_character_id, target_account_id,
                target_server, detail, state, outcome, requested_at, completed_at)
            VALUES (@RequestId, @realmId, @AdminName, @Action, @TargetCharacterId, @TargetAccountId, @TargetServer, @Detail,
                @state, @outcome, @now, @completedAt)
            ON CONFLICT (request_id) DO NOTHING
            RETURNING {Columns}
            """;

        var now = DbTime.ToUtc(clock.UtcNowMs);
        await using var connection = await realmDb.OpenConnectionAsync(ct);
        var created = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(sql, new
        {
            request.RequestId, realmId, request.AdminName, request.Action, request.TargetCharacterId, request.TargetAccountId,
            request.TargetServer, request.Detail,
            state = outcome == null ? AdminActionState.Pending : AdminActionState.Done,
            outcome = outcome?.ToString(),
            now,
            completedAt = outcome == null ? (DateTime?)null : now,
        }, cancellationToken: ct));
        if (created != null)
            return (AdminSubmitStatus.Created, created.ToRecord());

        var existing = await GetAsync(connection, request.RequestId, ct)
                       ?? throw new InvalidOperationException($"admin action {request.RequestId} conflicted but was not found");
        var same = existing.RealmId == realmId && existing.Action == request.Action && existing.AdminName == request.AdminName
                   && existing.TargetCharacterId == request.TargetCharacterId && existing.TargetAccountId == request.TargetAccountId;
        return (same ? AdminSubmitStatus.AlreadyRecorded : AdminSubmitStatus.Conflict, existing);
    }

    public async Task<AdminActionRecord?> GetAsync(Guid requestId, CancellationToken ct = default)
    {
        await using var connection = await realmDb.OpenConnectionAsync(ct);
        return await GetAsync(connection, requestId, ct);
    }

    /// <summary>The newest actions on a character, newest first.</summary>
    public async Task<IReadOnlyList<AdminActionRecord>> ListForCharacterAsync(long characterId, int limit, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT {Columns} FROM admin_actions
            WHERE realm_id = @realmId AND target_character_id = @characterId
            ORDER BY action_id DESC LIMIT @limit
            """;
        await using var connection = await realmDb.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<Row>(new CommandDefinition(sql, new { realmId, characterId, limit }, cancellationToken: ct));
        return rows.Select(r => r.ToRecord()).ToList();
    }

    /// <summary>The newest actions of this realm, newest first.</summary>
    public async Task<IReadOnlyList<AdminActionRecord>> ListRecentAsync(int limit, CancellationToken ct = default)
    {
        var sql = $"SELECT {Columns} FROM admin_actions WHERE realm_id = @realmId ORDER BY action_id DESC LIMIT @limit";
        await using var connection = await realmDb.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<Row>(new CommandDefinition(sql, new { realmId, limit }, cancellationToken: ct));
        return rows.Select(r => r.ToRecord()).ToList();
    }

    /// <summary>
    /// Claims up to <paramref name="max"/> pending commands addressed to <paramref name="serverId"/>, oldest first. Two
    /// claimers never get the same row (rows another transaction is claiming are skipped, and a claimed row is no longer
    /// pending).
    /// </summary>
    public async Task<IReadOnlyList<AdminCommand>> ClaimAsync(string serverId, int max, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE admin_actions SET state = 'claimed', claimed_at = @now, claimed_by = @serverId
            WHERE action_id IN (
                SELECT action_id FROM admin_actions
                WHERE realm_id = @realmId AND target_server = @serverId AND state = 'pending'
                ORDER BY action_id
                LIMIT @max
                FOR UPDATE SKIP LOCKED)
            RETURNING action_id AS ActionId, action AS Action, target_character_id AS CharacterId
            """;
        await using var connection = await realmDb.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<ClaimRow>(new CommandDefinition(sql,
            new { now = DbTime.ToUtc(clock.UtcNowMs), serverId, realmId, max }, cancellationToken: ct));
        return rows.OrderBy(r => r.ActionId).Select(r => new AdminCommand(r.ActionId, r.Action, r.CharacterId ?? 0)).ToList();
    }

    /// <summary>
    /// Writes a claimed command's outcome. Applies once: false when the row is not claimed by
    /// <paramref name="serverId"/> or already has an outcome, except that the real outcome replaces an
    /// <see cref="AdminOutcome.Expired"/> written while the command was still running.
    /// </summary>
    public async Task<bool> CompleteAsync(long actionId, string serverId, AdminOutcome outcome, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE admin_actions SET state = 'done', outcome = @outcome, completed_at = @now
            WHERE action_id = @actionId AND realm_id = @realmId AND claimed_by = @serverId
              AND (state = 'claimed' OR (state = 'done' AND outcome = 'Expired'))
            """;
        await using var connection = await realmDb.OpenConnectionAsync(ct);
        var changed = await connection.ExecuteAsync(new CommandDefinition(sql,
            new { actionId, realmId, serverId, outcome = outcome.ToString(), now = DbTime.ToUtc(clock.UtcNowMs) }, cancellationToken: ct));
        return changed == 1;
    }

    /// <summary>
    /// Closes this realm's commands that were requested more than <paramref name="expiryMs"/> ago and are not done (their
    /// server crashed, stopped, or never existed) with <see cref="AdminOutcome.Expired"/>. Returns how many.
    /// </summary>
    public async Task<int> ExpireAsync(int expiryMs, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE admin_actions SET state = 'done', outcome = 'Expired', completed_at = @now
            WHERE realm_id = @realmId AND state <> 'done' AND requested_at < @cutoff
            """;
        var nowMs = clock.UtcNowMs;
        await using var connection = await realmDb.OpenConnectionAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition(sql,
            new { realmId, now = DbTime.ToUtc(nowMs), cutoff = DbTime.ToUtc(nowMs - expiryMs) }, cancellationToken: ct));
    }

    private static async Task<AdminActionRecord?> GetAsync(NpgsqlConnection connection, Guid requestId, CancellationToken ct)
    {
        var sql = $"SELECT {Columns} FROM admin_actions WHERE request_id = @requestId";
        var row = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(sql, new { requestId }, cancellationToken: ct));
        return row?.ToRecord();
    }

    private sealed class ClaimRow
    {
        public long ActionId { get; init; }
        public string Action { get; init; } = "";
        public long? CharacterId { get; init; }
    }

    private sealed class Row
    {
        public long ActionId { get; init; }
        public Guid RequestId { get; init; }
        public int RealmId { get; init; }
        public string AdminName { get; init; } = "";
        public string Action { get; init; } = "";
        public long? TargetCharacterId { get; init; }
        public long? TargetAccountId { get; init; }
        public string? TargetServer { get; init; }
        public string? Detail { get; init; }
        public string State { get; init; } = "";
        public string? Outcome { get; init; }
        public DateTime RequestedAt { get; init; }
        public DateTime? ClaimedAt { get; init; }
        public string? ClaimedBy { get; init; }
        public DateTime? CompletedAt { get; init; }

        public AdminActionRecord ToRecord() => new(ActionId, RequestId, RealmId, AdminName, Action, TargetCharacterId,
            TargetAccountId, TargetServer, Detail, State, Outcome, DbTime.ToUtcMs(RequestedAt),
            ClaimedAt == null ? null : DbTime.ToUtcMs(ClaimedAt.Value), ClaimedBy,
            CompletedAt == null ? null : DbTime.ToUtcMs(CompletedAt.Value));
    }
}
