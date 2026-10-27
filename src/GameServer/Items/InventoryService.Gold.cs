using GameServer.Economy;
using GameServer.Events;
using GameServer.Persistence;

namespace GameServer.Items;

/// <summary>What an outpost sale paid for, carried by its gold entry so <see cref="ItemSold"/> goes out on commit.</summary>
public readonly record struct SaleInfo(ushort ItemNetId, byte Rarity, int Count, int NpcDefId);

/// <summary>The outcome of a gold operation. A repeated operation key returns the first result.</summary>
public readonly record struct GoldResult(bool Applied, bool Repeated, long Amount);

/// <summary>
/// Gold (WP-M2-4, spec 4.1, 4.2): <b>InventoryService is the only writer of gold</b> (architecture rule 4). Each character's
/// balance is held in memory next to its bag; a change is an operation like an item change (idempotency key, reason,
/// source, a <c>gold_ledger</c> row) that rides on the character's next save in the same transaction as its items and the
/// character row (rule 5), and is published (<see cref="GoldChanged"/>, <see cref="GoldDestroyed"/>, <see cref="ItemSold"/>)
/// only after that save commits.
/// <para>
/// Class 1 operations (R-7.1-01: outpost sale, vendor purchase) change items and gold here, then ask for an immediate save
/// with <see cref="RequestClass1Save"/>; the pending class 2 changes ride in the same transaction, so they can never commit
/// after the class 1 operation (the ordering barrier by inclusion, R-7.1-02). The caller confirms to the player only once
/// <see cref="IsCommitted"/> says so.
/// </para>
/// Kept in its own file so the item rework of WP-M2-1 merges cleanly; the main file only calls the three hooks
/// (<see cref="DetachGold"/>, <see cref="TakeGold"/>, <see cref="CommitGold"/>).
/// </summary>
public sealed partial class InventoryService
{
    private readonly Dictionary<long, GoldAccount> _gold = new();

    /// <summary>Most gold one character may hold (<c>EconomySettings.maxGold</c>); set when the world is built.</summary>
    public long MaxGold { get; set; } = long.MaxValue;

    /// <summary>Asks this character's save scheduler for an immediate save (set by the SessionManager).</summary>
    public Action<long>? RequestClass1 { get; set; }

    /// <summary>Starts holding a character's gold as loaded at login (after <see cref="Attach"/>).</summary>
    public void AttachGold(long characterId, long gold)
    {
        if (!_characters.ContainsKey(characterId))
            throw new InvalidOperationException($"character {characterId} has no inventory attached");
        if (gold < 0)
            throw new ArgumentOutOfRangeException(nameof(gold));
        _gold[characterId] = new GoldAccount(gold);
    }

    public bool HasGold(long characterId) => _gold.ContainsKey(characterId);

    /// <summary>Gold held by every attached character now (economy snapshots, R-4.2-07).</summary>
    public long GoldHeld()
    {
        long total = 0;
        foreach (var account in _gold.Values)
            total += account.Current;
        return total;
    }

    /// <summary>The balance as the world sees it now (including changes not yet saved).</summary>
    public long Gold(long characterId) => Account(characterId).Current;

    /// <summary>The balance as last committed to the database: what the player is shown (rule 5).</summary>
    public long CommittedGold(long characterId) => Account(characterId).Committed;

    /// <summary>
    /// Read-only: the stored result of an applied item operation (<see cref="TryAdd"/>: units added; <see cref="TryRemove"/>:
    /// units removed), so a repeated class 1 request can answer what the first one did.
    /// </summary>
    public bool TryGetOpResult(long characterId, string opKey, out int result)
    {
        result = 0;
        return _characters.TryGetValue(characterId, out var inventory) && inventory.SeenOps.TryGetValue(opKey, out result);
    }

    /// <summary>Read-only: the amount of an applied gold operation.</summary>
    public bool TryGetGoldOp(long characterId, string opKey, out long amount)
    {
        amount = 0;
        return _gold.TryGetValue(characterId, out var account) && account.Seen.TryGetValue(opKey, out amount);
    }

    /// <summary>Adds <paramref name="amount"/> gold (created: a faucet, R-4.2-01). Refused when it would pass <see cref="MaxGold"/>.</summary>
    public GoldResult TryAddGold(long characterId, long amount, string opKey, InventoryReason reason, string source,
        SaleInfo? sale = null)
    {
        if (amount <= 0)
            throw new ArgumentOutOfRangeException(nameof(amount));
        var account = Account(characterId);
        if (account.Seen.TryGetValue(opKey, out var previous))
            return new GoldResult(true, true, previous);
        if (amount > MaxGold - account.Current)
            return new GoldResult(false, false, 0);
        RecordGold(characterId, account, opKey, amount, reason, source, sale);
        return new GoldResult(true, false, amount);
    }

    /// <summary>Removes <paramref name="amount"/> gold (destroyed: a sink, R-4.2-04). Refused when the balance is lower.</summary>
    public GoldResult TryRemoveGold(long characterId, long amount, string opKey, InventoryReason reason, string source)
    {
        if (amount <= 0)
            throw new ArgumentOutOfRangeException(nameof(amount));
        var account = Account(characterId);
        if (account.Seen.TryGetValue(opKey, out var previous))
            return new GoldResult(true, true, previous);
        if (amount > account.Current)
            return new GoldResult(false, false, 0);
        RecordGold(characterId, account, opKey, -amount, reason, source, null);
        return new GoldResult(true, false, amount);
    }

    /// <summary>True when <paramref name="amount"/> more gold fits under <see cref="MaxGold"/>.</summary>
    public bool GoldFits(long characterId, long amount) => amount <= MaxGold - Account(characterId).Current;

    /// <summary>Saves this character's changes now (a class 1 operation, R-7.1-01).</summary>
    public void RequestClass1Save(long characterId) => RequestClass1?.Invoke(characterId);

    private void RecordGold(long characterId, GoldAccount account, string opKey, long delta, InventoryReason reason,
        string source, SaleInfo? sale)
    {
        var inventory = Get(characterId);
        account.Current += delta;
        account.Seen.Add(opKey, Math.Abs(delta));
        inventory.SeenOps.Add(opKey, 1);
        inventory.PendingOps.Add(new InventoryOpRecord(opKey, reason.ToString(), source));
        account.Pending.Add(new PendingGold(
            new GoldEntry(opKey, delta, account.Current, EconomyFlows.Of(reason, delta), reason.ToString(), source), reason, sale));
        MarkClass2?.Invoke(characterId);
    }

    private void DetachGold(long characterId) => _gold.Remove(characterId);

    /// <summary>TakeBatch hook: the pending gold entries, now in flight with save <paramref name="saveId"/>.</summary>
    private GoldBatch? TakeGold(long characterId, Guid saveId)
    {
        if (!_gold.TryGetValue(characterId, out var account) || account.Pending.Count == 0)
            return null;
        var entries = new List<GoldEntry>(account.Pending.Count);
        foreach (var pending in account.Pending)
            entries.Add(pending.Entry);
        account.InFlight = new InFlightGold(saveId, account.Current, account.Pending.ToList());
        account.Pending.Clear();
        return new GoldBatch(account.Current, entries);
    }

    /// <summary>Committed hook: save <paramref name="saveId"/> committed, so its gold is the player's and its events go out.</summary>
    private void CommitGold(long characterId, Guid saveId)
    {
        if (!_gold.TryGetValue(characterId, out var account) || account.InFlight is not { } inFlight || inFlight.SaveId != saveId)
            return;
        account.InFlight = null;
        account.Committed = inFlight.GoldAfter;
        if (_events == null)
            return;
        foreach (var pending in inFlight.Entries)
        {
            var entry = pending.Entry;
            _events.Publish(new GoldChanged(characterId, entry.Delta, entry.BalanceAfter, pending.Reason), characterId);
            if (entry.Flow == GoldFlow.Sink)
                _events.Publish(new GoldDestroyed(characterId, -entry.Delta, pending.Reason), characterId);
            if (pending.Sale is { } sale)
                _events.Publish(new ItemSold(characterId, sale.ItemNetId, sale.Rarity, sale.Count, entry.Delta, sale.NpcDefId),
                    characterId);
        }
    }

    private GoldAccount Account(long characterId) =>
        _gold.TryGetValue(characterId, out var account)
            ? account
            : throw new KeyNotFoundException($"character {characterId} has no gold attached");

    private readonly record struct PendingGold(GoldEntry Entry, InventoryReason Reason, SaleInfo? Sale);

    private sealed record InFlightGold(Guid SaveId, long GoldAfter, List<PendingGold> Entries);

    private sealed class GoldAccount(long gold)
    {
        public long Current { get; set; } = gold;
        public long Committed { get; set; } = gold;

        /// <summary>Gold operation keys applied since the character was attached, with their amount (rule 5).</summary>
        public Dictionary<string, long> Seen { get; } = new(StringComparer.Ordinal);

        public List<PendingGold> Pending { get; } = new();
        public InFlightGold? InFlight { get; set; }
    }
}
