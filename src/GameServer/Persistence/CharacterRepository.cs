using Dapper;
using Npgsql;
using Shared.Util;

namespace GameServer.Persistence;

/// <summary>
/// Characters in one realm database (R-2.1-03, R-2.1-05, R-7.1-04). Every query is filtered by realm.
/// Writes use optimistic concurrency on <c>version</c>: a stale version is a typed failure, never an overwrite.
/// The single-writer rule (R-2.1-06, R-7.1-02) is enforced by the ownership lock in <c>SessionManager</c>;
/// the version check is the database-side backstop.
/// Called from persistence workers, never from the tick thread (spec 1.6).
/// </summary>
public sealed class CharacterRepository(NpgsqlDataSource realmDb, int realmId, IClock clock) : ICharacterSaveStore
{
    private const long FirstVersion = 1;

    private const string NameKey = "characters_realm_name_key";
    private const string SlotKey = "characters_realm_account_slot_key";
    private const string RequestKey = "characters_create_request_key";

    private const string Columns = """
        character_id AS CharacterId, realm_id AS RealmId, account_id AS AccountId, slot AS Slot, name AS Name,
        class_id AS ClassId, level AS Level, exp AS Exp, world_id AS WorldId, pos_x_cm AS PosXCm, pos_y_cm AS PosYCm,
        pos_z_cm AS PosZCm, facing AS Facing, hp AS Hp, mp AS Mp, stamina AS Stamina, toxicity_milli AS ToxicityMilli,
        satiety_milli AS SatietyMilli, consumption_at AS ConsumptionAt, version AS Version,
        create_request_id AS CreateRequestId, created_at AS CreatedAt, updated_at AS UpdatedAt,
        deleted_at AS DeletedAt, purge_after AS PurgeAfter, gold AS Gold,
        rank AS Rank, skill_points AS SkillPoints
        """;

    public int RealmId => realmId;

    public async Task<CreateCharacterResult> CreateAsync(NewCharacter request, CancellationToken ct = default)
    {
        const string sql = $"""
            INSERT INTO characters (realm_id, account_id, slot, name, class_id, level, exp, world_id,
                pos_x_cm, pos_y_cm, pos_z_cm, facing, hp, mp, stamina, version, create_request_id, created_at, updated_at,
                rank, skill_points)
            VALUES (@realmId, @AccountId, @Slot, @Name, @ClassId, @Level, @Exp, @WorldId,
                @PosXCm, @PosYCm, @PosZCm, @Facing, @Hp, @Mp, @Stamina, @version, @RequestId, @now, @now,
                @Rank, @SkillPoints)
            RETURNING {Columns}
            """;

        var s = request.Initial;
        var parameters = new
        {
            realmId,
            request.AccountId,
            Slot = (short)request.Slot,
            request.Name,
            request.ClassId,
            s.Level, s.Exp, s.WorldId, s.PosXCm, s.PosYCm, s.PosZCm, s.Facing, s.Hp, s.Mp, s.Stamina,
            version = FirstVersion,
            request.RequestId,
            now = DbTime.ToUtc(clock.UtcNowMs),
            Rank = (short)s.Rank, s.SkillPoints,
        };

        await using var connection = await realmDb.OpenConnectionAsync(ct);
        try
        {
            var row = await connection.QuerySingleAsync<CharacterRow>(new CommandDefinition(sql, parameters, cancellationToken: ct));
            return new CreateCharacterResult(CreateCharacterStatus.Created, row.ToRecord());
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation && e.ConstraintName is RequestKey or NameKey or SlotKey)
        {
            // Double submit first: a racing duplicate of a create can trip the name or slot key before the
            // request key, so look for the request ID whichever key was reported.
            var existing = await connection.QuerySingleOrDefaultAsync<CharacterRow>(new CommandDefinition(
                $"SELECT {Columns} FROM characters WHERE create_request_id = @RequestId", new { request.RequestId }, cancellationToken: ct));
            if (existing != null)
            {
                return existing.AccountId == request.AccountId && existing.RealmId == realmId
                    ? new CreateCharacterResult(CreateCharacterStatus.Created, existing.ToRecord())
                    : new CreateCharacterResult(CreateCharacterStatus.RequestIdReused, null);
            }

            return new CreateCharacterResult(e.ConstraintName == NameKey ? CreateCharacterStatus.NameTaken : CreateCharacterStatus.SlotTaken, null);
        }
    }

    /// <summary>Loads a character, including one pending deletion (the caller decides whether it may enter the world).</summary>
    public async Task<CharacterRecord?> LoadAsync(long characterId, CancellationToken ct = default)
    {
        await using var connection = await realmDb.OpenConnectionAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<CharacterRow>(new CommandDefinition(
            $"SELECT {Columns} FROM characters WHERE character_id = @characterId AND realm_id = @realmId",
            new { characterId, realmId }, cancellationToken: ct));
        return row?.ToRecord();
    }

    /// <summary>The account's characters in this realm by slot, including those pending deletion (R-9.10-04 cards).</summary>
    public async Task<IReadOnlyList<CharacterRecord>> ListByAccountAsync(long accountId, CancellationToken ct = default)
    {
        await using var connection = await realmDb.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<CharacterRow>(new CommandDefinition(
            $"SELECT {Columns} FROM characters WHERE account_id = @accountId AND realm_id = @realmId ORDER BY slot",
            new { accountId, realmId }, cancellationToken: ct));
        return rows.Select(r => r.ToRecord()).ToList();
    }

    /// <summary>
    /// One transaction (rule 5): the version-checked row update, then the inventory batch if any
    /// (<see cref="InventoryRepository.WriteAsync"/>). If any step fails nothing is written; a commit whose outcome is
    /// unknown is recognised on retry by <paramref name="saveId"/>, so the batch is never applied twice.
    /// </summary>
    public async Task<SaveResult> SaveAsync(long characterId, CharacterState state, long expectedVersion, Guid saveId,
        InventoryBatch? inventory = null, CancellationToken ct = default, ProgressionBatch? progression = null)
    {
        const string update = """
            UPDATE characters SET
                level = @Level, exp = @Exp, world_id = @WorldId, pos_x_cm = @PosXCm, pos_y_cm = @PosYCm, pos_z_cm = @PosZCm,
                facing = @Facing, hp = @Hp, mp = @Mp, stamina = @Stamina, rank = @Rank, skill_points = @SkillPoints,
                toxicity_milli = @ToxicityMilli, satiety_milli = @SatietyMilli, consumption_at = @ConsumptionAt,
                version = version + 1, last_save_id = @saveId, updated_at = @now
            WHERE character_id = @characterId AND realm_id = @realmId AND version = @expectedVersion AND deleted_at IS NULL
            RETURNING version
            """;

        var now = DbTime.ToUtc(clock.UtcNowMs);
        var parameters = new
        {
            state.Level, state.Exp, state.WorldId, state.PosXCm, state.PosYCm, state.PosZCm,
            state.Facing, state.Hp, state.Mp, state.Stamina,
            Rank = (short)state.Rank, state.SkillPoints,
            state.ToxicityMilli, state.SatietyMilli,
            ConsumptionAt = state.ConsumptionAtUtcMs == 0 ? (DateTime?)null : DbTime.ToUtc(state.ConsumptionAtUtcMs),
            saveId,
            now,
            characterId,
            realmId,
            expectedVersion,
        };

        await using var connection = await realmDb.OpenConnectionAsync(ct);
        await using (var transaction = await connection.BeginTransactionAsync(ct))
        {
            var newVersion = await connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(update, parameters, transaction, cancellationToken: ct));
            if (newVersion.HasValue)
            {
                if (inventory is { IsEmpty: false })
                {
                    try
                    {
                        await InventoryRepository.WriteAsync(connection, transaction, characterId, inventory, saveId, now, ct);
                    }
                    catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation
                                                      && e.ConstraintName == InventoryRepository.OpKeyConstraint)
                    {
                        await transaction.RollbackAsync(CancellationToken.None);
                        return new SaveResult(SaveStatus.DuplicateOperation, expectedVersion);
                    }
                }
                // WP-M2-3 progression: achievement counters, kill counts and rank-ups (spec 3.6), same transaction.
                if (progression is { IsEmpty: false })
                {
                    try
                    {
                        await ProgressionRepository.WriteAsync(connection, transaction, characterId, progression, saveId, now, ct);
                    }
                    catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation
                                                      && e.ConstraintName is ProgressionRepository.OpKeyConstraint
                                                          or ProgressionRepository.StepConstraint)
                    {
                        await transaction.RollbackAsync(CancellationToken.None);
                        return new SaveResult(SaveStatus.DuplicateOperation, expectedVersion);
                    }
                }
                // End WP-M2-3 progression.
                await transaction.CommitAsync(ct);
                return new SaveResult(SaveStatus.Saved, newVersion.Value);
            }
            await transaction.RollbackAsync(ct);
        }

        // Nothing written. Find out why, without writing anything.
        var current = await connection.QuerySingleOrDefaultAsync<SaveCheckRow>(new CommandDefinition(
            "SELECT version AS Version, last_save_id AS LastSaveId, deleted_at AS DeletedAt FROM characters WHERE character_id = @characterId AND realm_id = @realmId",
            new { characterId, realmId }, cancellationToken: ct));

        if (current == null)
            return new SaveResult(SaveStatus.NotFound, 0);
        if (current.LastSaveId == saveId)
            return new SaveResult(SaveStatus.Saved, current.Version); // a retry of a save that already committed
        if (current.DeletedAt.HasValue)
            return new SaveResult(SaveStatus.Deleted, current.Version);
        return new SaveResult(SaveStatus.VersionConflict, current.Version);
    }

    /// <summary>
    /// Soft deletes the character (R-2.1-05). The caller passes the end of the grace period.
    /// Bumps the version, so a save from a stale in-memory copy fails instead of resurrecting it.
    /// TODO(open): restoring a deleted character (spec 10, M4). Purging after <c>purge_after</c> belongs to Workers.
    /// </summary>
    public async Task<DeleteStatus> SoftDeleteAsync(long characterId, long accountId, long purgeAfterUtcMs, CancellationToken ct = default)
    {
        const string update = """
            UPDATE characters SET deleted_at = @now, purge_after = @purgeAfter, version = version + 1,
                last_save_id = NULL, updated_at = @now
            WHERE character_id = @characterId AND realm_id = @realmId AND account_id = @accountId AND deleted_at IS NULL
            """;

        var now = clock.UtcNowMs;
        if (purgeAfterUtcMs < now)
            throw new ArgumentOutOfRangeException(nameof(purgeAfterUtcMs), "the grace period cannot end in the past");

        await using var connection = await realmDb.OpenConnectionAsync(ct);
        var updated = await connection.ExecuteAsync(new CommandDefinition(update,
            new { now = DbTime.ToUtc(now), purgeAfter = DbTime.ToUtc(purgeAfterUtcMs), characterId, realmId, accountId }, cancellationToken: ct));
        if (updated == 1)
            return DeleteStatus.Deleted;

        var alreadyDeleted = await connection.ExecuteScalarAsync<bool?>(new CommandDefinition(
            "SELECT deleted_at IS NOT NULL FROM characters WHERE character_id = @characterId AND realm_id = @realmId AND account_id = @accountId",
            new { characterId, realmId, accountId }, cancellationToken: ct));
        return alreadyDeleted == true ? DeleteStatus.AlreadyDeleted : DeleteStatus.NotFound;
    }

    private sealed class SaveCheckRow
    {
        public long Version { get; init; }
        public Guid? LastSaveId { get; init; }
        public DateTime? DeletedAt { get; init; }
    }

    private sealed class CharacterRow
    {
        public long CharacterId { get; init; }
        public int RealmId { get; init; }
        public long AccountId { get; init; }
        public short Slot { get; init; }
        public string Name { get; init; } = "";
        public string ClassId { get; init; } = "";
        public int Level { get; init; }
        public long Exp { get; init; }
        public string WorldId { get; init; } = "";
        public int PosXCm { get; init; }
        public int PosYCm { get; init; }
        public int PosZCm { get; init; }
        public int Facing { get; init; }
        public int Hp { get; init; }
        public int Mp { get; init; }
        public int Stamina { get; init; }
        public int ToxicityMilli { get; init; }
        public int SatietyMilli { get; init; }
        public DateTime? ConsumptionAt { get; init; }
        public long Version { get; init; }
        public Guid CreateRequestId { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime UpdatedAt { get; init; }
        public DateTime? DeletedAt { get; init; }
        public DateTime? PurgeAfter { get; init; }
        public long Gold { get; init; }
        public short Rank { get; init; }
        public int SkillPoints { get; init; }

        public CharacterRecord ToRecord() => new(
            CharacterId, RealmId, AccountId, Slot, Name, ClassId,
            new CharacterState(Level, Exp, WorldId, PosXCm, PosYCm, PosZCm, Facing, Hp, Mp, Stamina, (Shared.Simulation.Stats.Rank)Rank, SkillPoints,
                ToxicityMilli, SatietyMilli, ConsumptionAt.HasValue ? DbTime.ToUtcMs(ConsumptionAt.Value) : 0),
            Version,
            DbTime.ToUtcMs(CreatedAt),
            DbTime.ToUtcMs(UpdatedAt),
            DeletedAt.HasValue ? DbTime.ToUtcMs(DeletedAt.Value) : null,
            PurgeAfter.HasValue ? DbTime.ToUtcMs(PurgeAfter.Value) : null,
            Gold);
    }
}
