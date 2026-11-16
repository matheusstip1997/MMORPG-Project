using System.Data.Common;
using System.Text.Json;
using Dapper;
using Npgsql;
using Shared.Protocol;

namespace GameServer.Persistence;

/// <summary>
/// Contracts and portfolios in the realm database (spec 5.4, 7.1, 8.2; WP-M4-10): the <c>contracts</c>, <c>portfolio_counters</c>,
/// <c>portfolio_ops</c>, <c>portfolio_showcase</c> and <c>portfolio_settings</c> rows of migration V0025. A
/// <see cref="ContractBatch"/> is written inside the character save that carries it (<see cref="WriteAsync"/>: the customer's
/// proposal with its escrow hold; the crafter's acceptance, work started, completion and cancel); the customer's cancel and the
/// expiry close the row in the escrow's release-to-mail transaction (<see cref="CloseAsync"/>, the <see cref="IEscrowHolderSink"/>
/// of holder kind <see cref="EscrowHolderKind.Contract"/>). Only <c>Contracts/ContractService</c> produces them. Loads and portfolio
/// reads run off the tick thread (spec 1.6).
/// </summary>
/// <param name="expiryCostPercent">`ContractSettings.cancelCostPercent`: what a contract that expires after its crafter started
/// work costs that crafter (user answer 2026-11-16, the charge written by <see cref="CloseAsync"/>); 0 for tools and tests that
/// close no contract.</param>
public sealed class ContractRepository(NpgsqlDataSource realmDb, int expiryCostPercent = 0) : IEscrowHolderSink
{
    internal const string PrimaryKeyConstraint = "contracts_pkey";
    internal const string ProposeKeyConstraint = "contracts_propose_op_key";
    internal const string AcceptKeyConstraint = "contracts_accept_op_key";
    internal const string CloseKeyConstraint = "contracts_close_op_key";
    internal const string EscrowConstraint = "contracts_escrow";

    /// <summary>The key of the charge an expiry writes (its release key plus a suffix; the primary key of `contract_charges`).</summary>
    public static string ChargeKeyOf(string expiryOpKey) => expiryOpKey + ":charge";

    /// <summary>A crafter's pending charges (read at login, after the lock, with the character).</summary>
    public async Task<IReadOnlyList<ContractChargeRow>> LoadChargesAsync(long crafterId, CancellationToken ct = default)
    {
        await using var connection = await realmDb.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<(string Key, long ContractId, long CrafterId, long Amount)>(new CommandDefinition("""
            SELECT charge_key, contract_id, crafter_id, amount FROM contract_charges WHERE crafter_id = @crafterId AND state = 1
            ORDER BY created_at, charge_key
            """, new { crafterId }, cancellationToken: ct)))
            .Select(r => new ContractChargeRow(r.Key, r.ContractId, r.CrafterId, r.Amount)).ToList();
    }

    /// <summary>
    /// The charge an expiry committed, by its key (off the tick, after the release settled), or null when the expiry wrote none
    /// (the database's <c>work_started</c> was false). The world never recomputes it (review of PR #263).
    /// </summary>
    public async Task<ContractChargeFound?> FindChargeAsync(string chargeKey, CancellationToken ct = default)
    {
        await using var connection = await realmDb.OpenConnectionAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<(string Key, long ContractId, long CrafterId, long Amount, short State)?>(
            new CommandDefinition("""
                SELECT charge_key, contract_id, crafter_id, amount, state FROM contract_charges WHERE charge_key = @chargeKey
                """, new { chargeKey }, cancellationToken: ct));
        return row is { } r ? new ContractChargeFound(new ContractChargeRow(r.Key, r.ContractId, r.CrafterId, r.Amount), r.State == 1) : null;
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The open contracts (world start), the highest contract id ever written, and the acceptances since the UTC day began.</summary>
    public async Task<ContractLoad> LoadAsync(long dayStartUtcMs, CancellationToken ct = default)
    {
        await using var connection = await realmDb.OpenConnectionAsync(ct);
        var open = (await connection.QueryAsync<DbRow>(new CommandDefinition(Select + " WHERE state IN (1, 2) ORDER BY contract_id",
            cancellationToken: ct))).Select(r => r.ToRow()).ToList();
        long max = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COALESCE(MAX(contract_id), 0) FROM contracts", cancellationToken: ct));
        var today = (await connection.QueryAsync<(long Customer, long Crafter, DateTime AcceptedAt)>(new CommandDefinition("""
            SELECT customer_id, crafter_id, accepted_at FROM contracts WHERE accepted_at >= @since AND crafter_id IS NOT NULL
            """, new { since = DbTime.ToUtc(dayStartUtcMs) }, cancellationToken: ct)))
            .Select(r => (r.Customer, r.Crafter, DbTime.ToUtcMs(r.AcceptedAt))).ToList();
        return new ContractLoad(open, max, today);
    }

    /// <summary>One contract whatever its state (tests and admin tools); null when unknown.</summary>
    public async Task<ContractRow?> FindAsync(long contractId, CancellationToken ct = default)
    {
        await using var connection = await realmDb.OpenConnectionAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<DbRow>(new CommandDefinition(Select + " WHERE contract_id = @contractId",
            new { contractId }, cancellationToken: ct));
        return row?.ToRow();
    }

    /// <summary>A character's portfolio: its counters, its showcase and the hide flag (a view, off the tick).</summary>
    public async Task<PortfolioRecord> LoadPortfolioAsync(long characterId, CancellationToken ct = default)
    {
        await using var connection = await realmDb.OpenConnectionAsync(ct);
        bool hidden = await connection.ExecuteScalarAsync<bool?>(new CommandDefinition(
            "SELECT hidden FROM portfolio_settings WHERE character_id = @characterId", new { characterId }, cancellationToken: ct)) ?? false;
        var counters = (await connection.QueryAsync<(string JobId, short Category, short Rarity, short Outcome, long Count)>(
                new CommandDefinition("""
                    SELECT job_id, category, rarity, outcome, count FROM portfolio_counters WHERE character_id = @characterId
                    ORDER BY job_id, category, rarity, outcome
                    """, new { characterId }, cancellationToken: ct)))
            .Select(r => new PortfolioCounterRow(r.JobId, r.Category, r.Rarity, (PortfolioOutcome)r.Outcome, r.Count)).ToList();
        var showcase = (await connection.QueryAsync<(short Slot, string DefId, short Rarity, string Item, DateTime PinnedAt)>(
                new CommandDefinition("""
                    SELECT slot, def_id, rarity, item::text, pinned_at FROM portfolio_showcase WHERE character_id = @characterId
                    ORDER BY slot
                    """, new { characterId }, cancellationToken: ct)))
            .Select(r => new ShowcaseRow(r.Slot, r.DefId, r.Rarity, r.Item, DbTime.ToUtcMs(r.PinnedAt))).ToList();
        return new PortfolioRecord(characterId, hidden, counters, showcase);
    }

    /// <summary>
    /// Writes <paramref name="batch"/> inside the character save's transaction: proposals, then the guarded updates (a row that
    /// changed under one throws <see cref="ContractConflictException"/> and the caller rolls the whole save back), then the
    /// portfolio increments (once per operation key), the showcase and the hide flag.
    /// </summary>
    internal static async Task WriteAsync(DbConnection connection, DbTransaction transaction, long characterId, ContractBatch batch,
        Guid saveId, CancellationToken ct)
    {
        if (batch.Hidden is bool hidden)
            await WriteHiddenAsync(connection, transaction, characterId, hidden, ct);
        foreach (var row in batch.Proposals)
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO contracts (contract_id, propose_op_key, customer_id, escrow_id, kind, def_net_id, def_id, job_id,
                    output_category, gold, deadline_hours, terms, proposed_at, open_until, state, propose_save_id)
                VALUES (@ContractId, @ProposeOpKey, @CustomerId, @EscrowId, @Kind, @DefNetId, @DefId, @JobId, @OutputCategory, @Gold,
                    @DeadlineHours, @Terms::jsonb, @ProposedAt, @OpenUntil, 1, @saveId)
                """, new
            {
                row.ContractId, row.ProposeOpKey, row.CustomerId, row.EscrowId, Kind = (short)row.Kind, row.DefNetId, row.DefId,
                row.JobId, row.OutputCategory, row.Gold, DeadlineHours = (short)row.DeadlineHours, Terms = TermsJson(row.Slots),
                ProposedAt = DbTime.ToUtc(row.ProposedUtcMs), OpenUntil = DbTime.ToUtc(row.OpenUntilUtcMs), saveId,
            }, transaction, cancellationToken: ct));
        }
        foreach (var write in batch.Writes)
        {
            var at = DbTime.ToUtc(write.AtUtcMs);
            if (write.Kind == ContractWriteKind.WorkStarted)
            {
                // Never cleared and harmless when the contract closed meanwhile: no conflict, nothing else changes.
                await connection.ExecuteAsync(new CommandDefinition("""
                    UPDATE contracts SET work_started = true WHERE contract_id = @ContractId AND state = 2 AND crafter_id = @CrafterId
                    """, new { write.ContractId, write.CrafterId }, transaction, cancellationToken: ct));
                continue;
            }
            int updated = write.Kind switch
            {
                ContractWriteKind.Accept => await connection.ExecuteAsync(new CommandDefinition("""
                    UPDATE contracts SET state = 2, crafter_id = @CrafterId, accepted_at = @at, deadline_at = @deadline,
                        accept_op_key = @OpKey, accept_save_id = @saveId
                    WHERE contract_id = @ContractId AND state = 1
                    """, new { write.CrafterId, at, deadline = DbTime.ToUtc(write.DeadlineUtcMs), write.OpKey, saveId, write.ContractId },
                    transaction, cancellationToken: ct)),
                ContractWriteKind.Complete or ContractWriteKind.CrafterCancel => await connection.ExecuteAsync(new CommandDefinition("""
                    UPDATE contracts SET state = @State, closed_at = @at, close_op_key = @OpKey, close_save_id = @saveId, fee = @Fee,
                        cost = @Cost
                    WHERE contract_id = @ContractId AND state = 2 AND crafter_id = @CrafterId
                    """, new
                {
                    State = (short)(write.Kind == ContractWriteKind.Complete ? ContractDbState.Completed : ContractDbState.Cancelled),
                    at, write.OpKey, saveId, write.Fee, write.Cost, write.ContractId, write.CrafterId,
                }, transaction, cancellationToken: ct)),
                _ => throw new ArgumentOutOfRangeException(nameof(batch), $"unknown contract write {write.Kind}"),
            };
            if (updated != 1)
                throw new ContractConflictException(write.ContractId);
        }
        foreach (var charge in batch.Charges ?? Array.Empty<ContractChargeWrite>())
        {
            // The collection of a charge (user answers 2026-11-16): once, guarded by its pending state.
            int collected = await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE contract_charges SET state = 2, collected_amount = @Taken, collected_at = @at, collect_save_id = @saveId
                WHERE charge_key = @ChargeKey AND state = 1
                """, new { charge.Taken, at = DbTime.ToUtc(charge.AtUtcMs), saveId, charge.ChargeKey }, transaction, cancellationToken: ct));
            if (collected != 1)
                throw new ContractConflictException(0);
        }
        foreach (var counter in batch.Counters)
            await CountAsync(connection, transaction, counter, ct);
        foreach (var pin in batch.Showcase)
        {
            if (pin.ItemJson == null)
                await connection.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM portfolio_showcase WHERE character_id = @CharacterId AND slot = @Slot",
                    new { pin.CharacterId, Slot = (short)pin.Slot }, transaction, cancellationToken: ct));
            else
                await connection.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO portfolio_showcase (character_id, slot, def_id, rarity, item, pinned_at)
                    VALUES (@CharacterId, @Slot, @DefId, @Rarity, @ItemJson::jsonb, @at)
                    ON CONFLICT (character_id, slot) DO UPDATE SET def_id = EXCLUDED.def_id, rarity = EXCLUDED.rarity,
                        item = EXCLUDED.item, pinned_at = EXCLUDED.pinned_at
                    """, new { pin.CharacterId, Slot = (short)pin.Slot, pin.DefId, Rarity = (short)pin.Rarity, pin.ItemJson,
                    at = DbTime.ToUtc(pin.AtUtcMs) }, transaction, cancellationToken: ct));
        }
    }

    /// <summary>Writes the hide flag of <paramref name="characterId"/> in the character save's transaction.</summary>
    private static Task WriteHiddenAsync(DbConnection connection, DbTransaction transaction, long characterId, bool hidden,
        CancellationToken ct) =>
        connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO portfolio_settings (character_id, hidden) VALUES (@characterId, @hidden)
            ON CONFLICT (character_id) DO UPDATE SET hidden = EXCLUDED.hidden
            """, new { characterId, hidden }, transaction, cancellationToken: ct));

    /// <summary>
    /// <see cref="IEscrowHolderSink"/>: the customer's cancel or an expiry closes the open contract held by
    /// <paramref name="escrowId"/> in the escrow's release-to-mail transaction; an accepted contract's expiry counts as expired in
    /// its crafter's portfolio in the same transaction. False (nothing written) when no open contract holds it.
    /// </summary>
    public async Task<bool> CloseAsync(DbConnection connection, DbTransaction transaction, string escrowId, EscrowHolderKind holderKind,
        EscrowHolderClose close, string opKey, DateTime at, CancellationToken ct)
    {
        if (holderKind != EscrowHolderKind.Contract)
            throw new InvalidOperationException($"escrow {escrowId} is held by a {holderKind}, not a contract");
        if (close.State is not ((short)ContractDbState.Cancelled or (short)ContractDbState.Expired))
            throw new ArgumentOutOfRangeException(nameof(close), $"a contract closes by mail as cancelled or expired, not {close.State}");
        var closed = await connection.QuerySingleOrDefaultAsync<(long ContractId, long? Crafter, string JobId, short Category, bool WorkStarted, long Gold)?>(
            new CommandDefinition("""
            UPDATE contracts SET state = @State, closed_at = @at, close_op_key = @opKey
            WHERE escrow_id = @escrowId AND state IN (1, 2)
            RETURNING contract_id, crafter_id, job_id, output_category, work_started, gold
            """, new { close.State, at, opKey, escrowId }, transaction, cancellationToken: ct));
        if (closed is not { } row)
            return false;
        if (row.Crafter is long crafter && close.State == (short)ContractDbState.Expired)
        {
            await CountAsync(connection, transaction, new PortfolioIncrement(crafter, row.JobId, row.Category, 0,
                PortfolioOutcome.ContractExpired, opKey + ":portfolio", DbTime.ToUtcMs(at)), ct);
            // `Yes, charge on expiry` (user, 2026-11-16): after work started the crafter owes the cancel cost; the charge commits
            // with the close and is collected in the crafter's own save (ContractService), never written to the crafter here.
            long amount = row.WorkStarted ? Shared.Simulation.Contracts.ContractMath.CancelCost(row.Gold, expiryCostPercent) : 0;
            if (amount > 0)
                await connection.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO contract_charges (charge_key, contract_id, crafter_id, amount, state, created_at)
                    VALUES (@key, @ContractId, @crafter, @amount, 1, @at) ON CONFLICT (charge_key) DO NOTHING
                    """, new { key = ChargeKeyOf(opKey), row.ContractId, crafter, amount, at }, transaction, cancellationToken: ct));
        }
        return true;
    }

    /// <summary>One portfolio increment, once per operation key.</summary>
    private static async Task CountAsync(DbConnection connection, DbTransaction transaction, PortfolioIncrement counter, CancellationToken ct)
    {
        int fresh = await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO portfolio_ops (op_key, character_id, counted_at) VALUES (@OpKey, @CharacterId, @at) ON CONFLICT (op_key) DO NOTHING
            """, new { counter.OpKey, counter.CharacterId, at = DbTime.ToUtc(counter.AtUtcMs) }, transaction, cancellationToken: ct));
        if (fresh == 0)
            return; // counted before (a repeated event)
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO portfolio_counters (character_id, job_id, category, rarity, outcome, count)
            VALUES (@CharacterId, @JobId, @Category, @Rarity, @Outcome, 1)
            ON CONFLICT (character_id, job_id, category, rarity, outcome) DO UPDATE SET count = portfolio_counters.count + 1
            """, new { counter.CharacterId, counter.JobId, counter.Category, counter.Rarity, Outcome = (short)counter.Outcome },
            transaction, cancellationToken: ct));
    }

    /// <summary>The terms as stored (camelCase JSON array).</summary>
    public static string TermsJson(IReadOnlyList<ContractSlotRow> slots) => JsonSerializer.Serialize(slots.Select(s => new TermsSlot
    {
        Supplier = (byte)s.Supplier, ItemNetId = s.ItemNetId, Rarity = s.Rarity, Count = s.Count,
    }), Json);

    private static IReadOnlyList<ContractSlotRow> ParseTerms(string json) =>
        (JsonSerializer.Deserialize<List<TermsSlot>>(json, Json) ?? new List<TermsSlot>())
        .Select(s => new ContractSlotRow((ContractSupplier)s.Supplier, s.ItemNetId, s.Rarity, s.Count)).ToList();

    private const string Select = """
        SELECT contract_id AS ContractId, propose_op_key AS ProposeOpKey, customer_id AS CustomerId, crafter_id AS CrafterId,
               escrow_id AS EscrowId, kind AS Kind, def_net_id AS DefNetId, def_id AS DefId, job_id AS JobId,
               output_category AS OutputCategory, gold AS Gold, deadline_hours AS DeadlineHours, terms::text AS Terms,
               proposed_at AS ProposedAt, open_until AS OpenUntil, accepted_at AS AcceptedAt, deadline_at AS DeadlineAt,
               work_started AS WorkStarted, state AS State
        FROM contracts
        """;

    private sealed class TermsSlot
    {
        public byte Supplier { get; set; }
        public int ItemNetId { get; set; }
        public int Rarity { get; set; }
        public int Count { get; set; }
    }

    private sealed class DbRow
    {
        public long ContractId { get; init; }
        public string ProposeOpKey { get; init; } = "";
        public long CustomerId { get; init; }
        public long? CrafterId { get; init; }
        public string EscrowId { get; init; } = "";
        public short Kind { get; init; }
        public int DefNetId { get; init; }
        public string DefId { get; init; } = "";
        public string JobId { get; init; } = "";
        public short OutputCategory { get; init; }
        public long Gold { get; init; }
        public short DeadlineHours { get; init; }
        public string Terms { get; init; } = "[]";
        public DateTime ProposedAt { get; init; }
        public DateTime OpenUntil { get; init; }
        public DateTime? AcceptedAt { get; init; }
        public DateTime? DeadlineAt { get; init; }
        public bool WorkStarted { get; init; }
        public short State { get; init; }

        public ContractRow ToRow() => new(ContractId, ProposeOpKey, CustomerId, CrafterId ?? 0, EscrowId, (ForgeKind)Kind, DefNetId, DefId,
            JobId, OutputCategory, Gold, DeadlineHours, ParseTerms(Terms), DbTime.ToUtcMs(ProposedAt), DbTime.ToUtcMs(OpenUntil),
            AcceptedAt is { } accepted ? DbTime.ToUtcMs(accepted) : 0, DeadlineAt is { } deadline ? DbTime.ToUtcMs(deadline) : 0,
            WorkStarted, (ContractDbState)State);
    }
}

/// <summary>
/// The <see cref="IEscrowHolderSink"/> of the realm (WP-M4-10): closes the holder of an escrow released to mail by its kind, the
/// listings' and stalls' rows (WP-M4-7, -8) or a contract's.
/// </summary>
public sealed class EscrowHolderSinks(IEscrowHolderSink listings, IEscrowHolderSink contracts) : IEscrowHolderSink
{
    public Task<bool> CloseAsync(DbConnection connection, DbTransaction transaction, string escrowId, EscrowHolderKind holderKind,
        EscrowHolderClose close, string opKey, DateTime at, CancellationToken ct) => holderKind == EscrowHolderKind.Contract
        ? contracts.CloseAsync(connection, transaction, escrowId, holderKind, close, opKey, at, ct)
        : listings.CloseAsync(connection, transaction, escrowId, holderKind, close, opKey, at, ct);
}
