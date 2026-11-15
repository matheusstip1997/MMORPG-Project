using System.Text.Json;
using Dapper;
using Npgsql;
using Shared.Data.Tables;
using Shared.Simulation.Economy;
using Shared.Util;

namespace GameServer.Persistence;

/// <summary>A threshold a pair of accounts newly crossed in a scan: what the <c>TransferFlagged</c> telemetry record carries.</summary>
public sealed record TransferFlagCrossing(long Day, long GiverAccount, long ReceiverAccount, long GiverCharacter, long ReceiverCharacter,
    long NetGold, long NetValue, int NewlyCrossed, int Crossed, int Transfers);

/// <summary>One row of the admin panel's transfer flag list (spec 7.4).</summary>
public sealed record TransferFlagRow(long FlagId, string Day, long GiverAccount, long ReceiverAccount, long GiverCharacterId,
    string? GiverName, long ReceiverCharacterId, string? ReceiverName, int Crossed, long NetGold, long NetValue, int Transfers,
    long FlaggedAtMs, long UpdatedAtMs);

/// <summary>
/// What one scan did: the transfers counted, of them those between one account's characters, the thresholds newly crossed, and
/// the op keys of the rows it could not read (recorded as counted with nothing added, so they never block a later scan).
/// </summary>
public sealed record TransferFlagScan(int Counted, int SameAccount, IReadOnlyList<TransferFlagCrossing> Crossings, IReadOnlyList<string> Unreadable);

/// <summary>
/// Large one-way transfers between accounts flagged (WP-M4-15; spec 4.5 R-4.5-02, 7.6; M4 decision 17; realm V0024). Reads the
/// committed transfers between two characters, never world state: trades (<c>transfer_ops</c>: the summary's items and gold
/// each way) and auction or stall purchases (<c>listing_sales</c> with its <c>listings</c> row: the seller gave the units, the
/// buyer the gold that reached the seller, the price less the tax). Each transfer is counted <b>once ever</b>
/// (<c>transfer_flag_ops</c>) into its pair of accounts' UTC day (<c>transfer_pair_days</c>), and a pair whose net gold or net
/// value (<see cref="TransferFlagMath"/>) reaches a <see cref="TransferFlagSettings"/> threshold gets one
/// <c>transfer_flags</c> row for the day. One transaction per scan under an advisory lock, so two servers never count a
/// transfer twice. A flag moves no value and changes no transfer. Off the tick only (spec 1.6).
/// </summary>
public sealed class TransferFlagRepository(NpgsqlDataSource realmDb, IClock clock)
{
    /// <summary>The advisory lock of the scan (one scan at a time per realm database).</summary>
    public const long ScanLock = 0x4D34_5F31_355F_5446; // "M4_15_TF"

    public const short SourceTrade = 1;
    public const short SourceListingSale = 2;

    private const string Epoch = "DATE '1970-01-01'";

    /// <summary>
    /// Counts the transfers committed within <see cref="TransferFlagSettings.LookbackMs"/> that no scan counted yet, at most
    /// <see cref="TransferFlagSettings.ScanBatchMax"/>, oldest first, and returns the thresholds they newly crossed. Run again
    /// until it counts nothing to catch up.
    /// </summary>
    public async Task<TransferFlagScan> ScanAsync(TransferFlagSettings settings, ItemValue? values, CancellationToken ct = default)
    {
        long nowMs = clock.UtcNowMs;
        var now = DbTime.ToUtc(nowMs);
        var since = DbTime.ToUtc(nowMs - settings.LookbackMs);
        await using var connection = await realmDb.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(@lockId)", new { lockId = ScanLock },
            transaction, cancellationToken: ct));

        var trades = await connection.QueryAsync<TradeRow>(new CommandDefinition($"""
            SELECT t.op_key AS OpKey, t.created_at AS At, (t.created_at AT TIME ZONE 'UTC')::date - {Epoch} AS Day,
                   t.character_a AS CharacterA, t.character_b AS CharacterB, ca.account_id AS AccountA, cb.account_id AS AccountB,
                   t.summary::text AS Summary
            FROM transfer_ops t
            JOIN characters ca ON ca.character_id = t.character_a
            JOIN characters cb ON cb.character_id = t.character_b
            WHERE t.created_at >= @since AND t.kind = @tradeKind
              AND NOT EXISTS (SELECT 1 FROM transfer_flag_ops f WHERE f.source = @source AND f.op_key = t.op_key)
            ORDER BY t.created_at, t.op_key
            LIMIT @limit
            """, new { since, source = SourceTrade, tradeKind = (short)TransferKind.Trade, limit = settings.ScanBatchMax }, transaction, cancellationToken: ct));
        var sales = await connection.QueryAsync<SaleRow>(new CommandDefinition($"""
            SELECT s.op_key AS OpKey, s.sold_at AS At, (s.sold_at AT TIME ZONE 'UTC')::date - {Epoch} AS Day,
                   l.seller_id AS Seller, s.buyer_id AS Buyer, cs.account_id AS SellerAccount, cb.account_id AS BuyerAccount,
                   l.def_id AS DefId, l.rarity AS Rarity, s.count AS Count, s.price AS Price, s.tax AS Tax
            FROM listing_sales s
            JOIN listings l ON l.listing_id = s.listing_id
            JOIN characters cs ON cs.character_id = l.seller_id
            JOIN characters cb ON cb.character_id = s.buyer_id
            WHERE s.sold_at >= @since
              AND NOT EXISTS (SELECT 1 FROM transfer_flag_ops f WHERE f.source = @source AND f.op_key = s.op_key)
            ORDER BY s.sold_at, s.op_key
            LIMIT @limit
            """, new { since, source = SourceListingSale, limit = settings.ScanBatchMax }, transaction, cancellationToken: ct));

        var transfers = trades.Select(t => FromTrade(t, values)).Concat(sales.Select(s => FromSale(s, values)))
            .OrderBy(t => t.At).ThenBy(t => t.Source).ThenBy(t => t.OpKey, StringComparer.Ordinal)
            .Take(settings.ScanBatchMax).ToList();

        int counted = 0, sameAccount = 0;
        var crossings = new List<TransferFlagCrossing>();
        var unreadable = new List<string>();
        foreach (var transfer in transfers)
        {
            int inserted = await connection.ExecuteAsync(new CommandDefinition($"""
                INSERT INTO transfer_flag_ops (source, op_key, day, scanned_at) VALUES (@Source, @OpKey, {Epoch} + @Day, @now)
                ON CONFLICT DO NOTHING
                """, new { transfer.Source, transfer.OpKey, Day = (int)transfer.Day, now }, transaction, cancellationToken: ct));
            if (inserted == 0)
                continue;
            counted++;
            if (transfer.Unreadable)
            {
                unreadable.Add(transfer.OpKey); // counted with nothing added: one bad row never blocks the rows after it
                continue;
            }
            if (transfer.FromAccount == transfer.ToAccount)
            {
                sameAccount++; // R-4.5-02 flags transfers between accounts: a character's own alts are left out
                continue;
            }
            var crossing = await CountAsync(connection, transaction, transfer, settings, now, ct);
            if (crossing != null)
                crossings.Add(crossing);
        }
        await transaction.CommitAsync(ct);
        return new TransferFlagScan(counted, sameAccount, crossings, unreadable);
    }

    /// <summary>The flags of the UTC days from <paramref name="fromDay"/> on (days since 1970-01-01), newest first, at most <paramref name="limit"/>.</summary>
    public async Task<IReadOnlyList<TransferFlagRow>> ListAsync(long fromDay, int limit, CancellationToken ct = default)
    {
        await using var connection = await realmDb.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<FlagRecord>(new CommandDefinition($"""
            SELECT f.flag_id AS FlagId, f.day::text AS Day, f.giver_account AS GiverAccount, f.receiver_account AS ReceiverAccount,
                   f.giver_character AS GiverCharacter, g.name AS GiverName, f.receiver_character AS ReceiverCharacter,
                   r.name AS ReceiverName, f.crossed AS Crossed, f.net_gold AS NetGold, f.net_value AS NetValue,
                   f.transfers AS Transfers, f.flagged_at AS FlaggedAt, f.updated_at AS UpdatedAt
            FROM transfer_flags f
            LEFT JOIN characters g ON g.character_id = f.giver_character
            LEFT JOIN characters r ON r.character_id = f.receiver_character
            WHERE f.day >= {Epoch} + @fromDay
            ORDER BY f.day DESC, f.flag_id DESC
            LIMIT @limit
            """, new { fromDay = (int)fromDay, limit }, cancellationToken: ct));
        return rows.Select(r => new TransferFlagRow(r.FlagId, r.Day, r.GiverAccount, r.ReceiverAccount, r.GiverCharacter, r.GiverName,
            r.ReceiverCharacter, r.ReceiverName, r.Crossed, r.NetGold, r.NetValue, r.Transfers, DbTime.ToUtcMs(r.FlaggedAt),
            DbTime.ToUtcMs(r.UpdatedAt))).ToList();
    }

    /// <summary>Adds one transfer to its pair's day and records the flag when the day is over a threshold.</summary>
    private static async Task<TransferFlagCrossing?> CountAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Transfer transfer, TransferFlagSettings settings, DateTime now, CancellationToken ct)
    {
        bool fromLow = transfer.FromAccount < transfer.ToAccount;
        long low = fromLow ? transfer.FromAccount : transfer.ToAccount, high = fromLow ? transfer.ToAccount : transfer.FromAccount;
        // "From" gave GoldOut and ItemValueOut; "to" gave GoldBack and ItemValueBack.
        var sums = await connection.QuerySingleAsync<PairDayRow>(new CommandDefinition($"""
            INSERT INTO transfer_pair_days (day, account_low, account_high, gold_low_to_high, gold_high_to_low,
                item_value_low_to_high, item_value_high_to_low, transfers, updated_at)
            VALUES ({Epoch} + @day, @low, @high, @goldLowToHigh, @goldHighToLow, @valueLowToHigh, @valueHighToLow, 1, @now)
            ON CONFLICT (day, account_low, account_high) DO UPDATE SET
                gold_low_to_high = transfer_pair_days.gold_low_to_high + EXCLUDED.gold_low_to_high,
                gold_high_to_low = transfer_pair_days.gold_high_to_low + EXCLUDED.gold_high_to_low,
                item_value_low_to_high = transfer_pair_days.item_value_low_to_high + EXCLUDED.item_value_low_to_high,
                item_value_high_to_low = transfer_pair_days.item_value_high_to_low + EXCLUDED.item_value_high_to_low,
                transfers = transfer_pair_days.transfers + 1,
                updated_at = EXCLUDED.updated_at
            RETURNING gold_low_to_high AS GoldLowToHigh, gold_high_to_low AS GoldHighToLow,
                      item_value_low_to_high AS ItemValueLowToHigh, item_value_high_to_low AS ItemValueHighToLow, transfers AS Transfers
            """, new
        {
            day = (int)transfer.Day, low, high,
            goldLowToHigh = fromLow ? transfer.GoldOut : transfer.GoldBack,
            goldHighToLow = fromLow ? transfer.GoldBack : transfer.GoldOut,
            valueLowToHigh = fromLow ? transfer.ItemValueOut : transfer.ItemValueBack,
            valueHighToLow = fromLow ? transfer.ItemValueBack : transfer.ItemValueOut,
            now,
        }, transaction, cancellationToken: ct));
        var day = new TransferPairDay(sums.GoldLowToHigh, sums.GoldHighToLow, sums.ItemValueLowToHigh, sums.ItemValueHighToLow, sums.Transfers);
        int crossed = TransferFlagMath.Crossed(day, settings.GoldPerPairDay, settings.ValuePerPairDay, settings.OneSidedGoldPercent);

        bool lowGives = TransferFlagMath.LowGives(day);
        long giver = lowGives ? low : high, receiver = lowGives ? high : low;
        long sign = lowGives ? 1 : -1;
        long netGold = sign * TransferFlagMath.NetGold(day), netValue = sign * TransferFlagMath.NetValue(day);
        bool fromGives = transfer.FromAccount == giver;
        long giverCharacter = fromGives ? transfer.FromCharacter : transfer.ToCharacter;
        long receiverCharacter = fromGives ? transfer.ToCharacter : transfer.FromCharacter;

        if (crossed == 0)
        {
            // Back under both thresholds: a flag the day already has stays (its crossed keeps what was ever crossed) and shows the
            // day's current net and transfers; no flag row is made (PR #258 review).
            await connection.ExecuteAsync(new CommandDefinition($"""
                UPDATE transfer_flags SET giver_account = @giver, receiver_account = @receiver, giver_character = @giverCharacter,
                    receiver_character = @receiverCharacter, net_gold = @netGold, net_value = @netValue, transfers = @transfers,
                    updated_at = @now
                WHERE day = {Epoch} + @day AND account_low = @low AND account_high = @high
                """, new
            {
                day = (int)transfer.Day, low, high, giver, receiver, giverCharacter, receiverCharacter, netGold, netValue,
                transfers = day.Transfers, now,
            }, transaction, cancellationToken: ct));
            return null;
        }

        int before = await connection.QuerySingleOrDefaultAsync<int?>(new CommandDefinition($"""
            SELECT crossed::int FROM transfer_flags WHERE day = {Epoch} + @day AND account_low = @low AND account_high = @high FOR UPDATE
            """, new { day = (int)transfer.Day, low, high }, transaction, cancellationToken: ct)) ?? 0;
        int all = before | crossed;
        await connection.ExecuteAsync(new CommandDefinition($"""
            INSERT INTO transfer_flags (day, account_low, account_high, giver_account, receiver_account, giver_character,
                receiver_character, crossed, net_gold, net_value, transfers, first_op_key, flagged_at, updated_at)
            VALUES ({Epoch} + @day, @low, @high, @giver, @receiver, @giverCharacter, @receiverCharacter, @all, @netGold, @netValue,
                @transfers, @opKey, @now, @now)
            ON CONFLICT ON CONSTRAINT transfer_flags_pair_day DO UPDATE SET
                giver_account = EXCLUDED.giver_account, receiver_account = EXCLUDED.receiver_account,
                giver_character = EXCLUDED.giver_character, receiver_character = EXCLUDED.receiver_character,
                crossed = EXCLUDED.crossed, net_gold = EXCLUDED.net_gold, net_value = EXCLUDED.net_value,
                transfers = EXCLUDED.transfers, updated_at = EXCLUDED.updated_at
            """, new
        {
            day = (int)transfer.Day, low, high, giver, receiver, giverCharacter, receiverCharacter, all = (short)all, netGold, netValue,
            transfers = day.Transfers, opKey = transfer.OpKey, now,
        }, transaction, cancellationToken: ct));
        int newly = crossed & ~before;
        return newly == 0 ? null : new TransferFlagCrossing(transfer.Day, giver, receiver, giverCharacter, receiverCharacter, netGold,
            netValue, newly, all, day.Transfers);
    }

    /// <summary>
    /// A trade: the summary's side "a" is the lower character id (<c>TradeService.Summary</c>). A summary that is not a JSON
    /// document is unreadable: the scan records it as counted with nothing added and reports it (PR #258 review).
    /// </summary>
    private static Transfer FromTrade(TradeRow row, ItemValue? values)
    {
        try
        {
            var (goldAtoB, itemsAtoB, goldBtoA, itemsBtoA) = ReadSummary(row.Summary, values);
            return new Transfer(SourceTrade, row.OpKey, row.At, row.Day, row.CharacterA, row.AccountA, row.CharacterB, row.AccountB,
                goldAtoB, itemsAtoB, goldBtoA, itemsBtoA);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            return new Transfer(SourceTrade, row.OpKey, row.At, row.Day, row.CharacterA, row.AccountA, row.CharacterB, row.AccountB,
                0, 0, 0, 0, Unreadable: true);
        }
    }

    /// <summary>A purchase: the seller gave the units, the buyer the gold that reached the seller (the price less the tax).</summary>
    private static Transfer FromSale(SaleRow row, ItemValue? values) =>
        new(SourceListingSale, row.OpKey, row.At, row.Day, row.Seller, row.SellerAccount, row.Buyer, row.BuyerAccount,
            0, values?.StackValue(row.DefId, row.Rarity, row.Count) ?? 0, Math.Max(0, row.Price - row.Tax), 0);

    /// <summary>Reads <c>{"aToB":{"items":[{"item","rarity","count",...}],"gold":n},"bToA":{...}}</c>.</summary>
    internal static (long GoldAtoB, long ItemsAtoB, long GoldBtoA, long ItemsBtoA) ReadSummary(string json, ItemValue? values)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new FormatException("a transfer summary is a JSON object");
        var (goldA, itemsA) = Side(root, "aToB", values);
        var (goldB, itemsB) = Side(root, "bToA", values);
        return (goldA, itemsA, goldB, itemsB);
    }

    private static (long Gold, long ItemValue) Side(JsonElement root, string name, ItemValue? values)
    {
        if (!root.TryGetProperty(name, out var side) || side.ValueKind != JsonValueKind.Object)
            return (0, 0);
        long gold = Math.Max(0, Long(side, "gold"));
        long value = 0;
        if (values != null && side.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                long id = Long(item, "item"), rarity = Long(item, "rarity");
                if (id is > 0 and <= int.MaxValue && rarity is >= 0 and <= int.MaxValue)
                    value += values.StackValue((int)id, (int)rarity, Long(item, "count"));
            }
        }
        return (gold, value);
    }

    /// <summary>A whole number field of an object; 0 when the element is not an object or the field is not a whole number.</summary>
    private static long Long(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long v) ? v : 0;

    /// <summary>
    /// One transfer: "from" gave <see cref="GoldOut"/> and items worth <see cref="ItemValueOut"/>, "to" gave the rest back;
    /// <see cref="Unreadable"/> when its row could not be read.
    /// </summary>
    private sealed record Transfer(short Source, string OpKey, DateTime At, long Day, long FromCharacter, long FromAccount,
        long ToCharacter, long ToAccount, long GoldOut, long ItemValueOut, long GoldBack, long ItemValueBack, bool Unreadable = false);

    private sealed class TradeRow
    {
        public string OpKey { get; init; } = "";
        public DateTime At { get; init; }
        public int Day { get; init; }
        public long CharacterA { get; init; }
        public long CharacterB { get; init; }
        public long AccountA { get; init; }
        public long AccountB { get; init; }
        public string Summary { get; init; } = "{}";
    }

    private sealed class SaleRow
    {
        public string OpKey { get; init; } = "";
        public DateTime At { get; init; }
        public int Day { get; init; }
        public long Seller { get; init; }
        public long Buyer { get; init; }
        public long SellerAccount { get; init; }
        public long BuyerAccount { get; init; }
        public string DefId { get; init; } = "";
        public short Rarity { get; init; }
        public int Count { get; init; }
        public long Price { get; init; }
        public long Tax { get; init; }
    }

    private sealed class PairDayRow
    {
        public long GoldLowToHigh { get; init; }
        public long GoldHighToLow { get; init; }
        public long ItemValueLowToHigh { get; init; }
        public long ItemValueHighToLow { get; init; }
        public int Transfers { get; init; }
    }

    private sealed class FlagRecord
    {
        public long FlagId { get; init; }
        public string Day { get; init; } = "";
        public long GiverAccount { get; init; }
        public long ReceiverAccount { get; init; }
        public long GiverCharacter { get; init; }
        public string? GiverName { get; init; }
        public long ReceiverCharacter { get; init; }
        public string? ReceiverName { get; init; }
        public short Crossed { get; init; }
        public long NetGold { get; init; }
        public long NetValue { get; init; }
        public int Transfers { get; init; }
        public DateTime FlaggedAt { get; init; }
        public DateTime UpdatedAt { get; init; }
    }
}
