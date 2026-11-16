using System.Collections.Concurrent;
using GameServer.Events;
using GameServer.Items;
using GameServer.Persistence;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Items;
using Shared.Util;

namespace GameServer.Contracts;

/// <summary>A character's portfolio writes waiting for its next save (merged into its contract batch).</summary>
public sealed record PortfolioPending(IReadOnlyList<PortfolioIncrement> Counters, IReadOnlyList<ShowcaseWrite> Showcase, bool? Hidden);

/// <summary>One portfolio view asked by a connection, answered once its read (off the world thread) came back.</summary>
public readonly record struct PortfolioAsk(long ViewerId, int Connection, uint RequestId, long TargetId, bool Own);

/// <summary>A finished portfolio read: the record, or null when it failed.</summary>
public readonly record struct PortfolioAnswer(PortfolioAsk Ask, PortfolioRecord? Record);

/// <summary>
/// The crafters' portfolios (spec 5.4 R-5.4-04, M4 decision 11; WP-M4-10), on the world thread. <b>Counters</b> per crafter, job,
/// item category, rarity and outcome: the crafter's own crafts, refines, conversions and enchant results come from the after-commit
/// events (<c>Crafted</c>, <c>Refined</c>, <c>Converted</c>, <c>Enchanted</c>) and ride the crafter's next save (class 2, once per
/// operation key); a contract's completion, the crafter's cancel and an expiry are counted by the contract's own transaction
/// (<c>ContractService</c>, <c>ContractRepository.CloseAsync</c>). The <b>showcase</b>: up to <c>ContractSettings.showcaseMax</c>
/// pinned snapshots of items the crafter made (read-only, kept when the item is lost), and the <b>hide flag</b>. Views read the
/// committed rows off the world thread (rule 5: only what committed is shown); a hidden portfolio is shown to its owner only.
/// </summary>
public sealed class PortfolioService : IEventConsumer<Crafted>, IEventConsumer<Refined>, IEventConsumer<Converted>,
    IEventConsumer<Enchanted>
{
    private readonly ContractSettings _settings;
    private readonly InventoryService _inventory;
    private readonly IClock _clock;
    private readonly Func<long, Task<PortfolioRecord>>? _read;
    private readonly Dictionary<long, Pending> _pending = new();
    private readonly ConcurrentQueue<PortfolioAnswer> _answers = new();
    private readonly Dictionary<long, List<PortfolioAsk>> _waitingCommit = new();
    private readonly Dictionary<long, List<PortfolioAsk>> _inFlightAsks = new();

    public PortfolioService(ContractSettings settings, InventoryService inventory, IClock clock, EventBus? events,
        Func<long, Task<PortfolioRecord>>? read)
    {
        _settings = settings;
        _inventory = inventory;
        _clock = clock;
        _read = read;
        if (events != null)
        {
            events.Subscribe<Crafted>(this);
            events.Subscribe<Refined>(this);
            events.Subscribe<Converted>(this);
            events.Subscribe<Enchanted>(this);
        }
    }

    /// <summary>Marks a character class 2 dirty with its save scheduler (set by the SessionManager).</summary>
    public Action<long>? MarkClass2 { get; set; }

    /// <summary>Asks a character's save at once (a pin, an unpin, the hide flag; set by the SessionManager).</summary>
    public Action<long, SaveReason>? RequestSave { get; set; }

    /// <summary>A portfolio's writes committed (set by the world: <c>PortfolioChanged</c>).</summary>
    public EventBus? Events { get; set; }

    public void Handle(in EventEnvelope envelope, in Crafted payload) =>
        Count(payload.CharacterId, payload.JobId, payload.OutputItemNetId, payload.Rarity, PortfolioOutcome.Crafted, payload.OpKey);

    public void Handle(in EventEnvelope envelope, in Refined payload) =>
        Count(payload.CharacterId, payload.JobId, payload.OutputItemNetId, 0, PortfolioOutcome.Refined, payload.OpKey);

    public void Handle(in EventEnvelope envelope, in Converted payload) =>
        Count(payload.CharacterId, payload.JobId, 0, 0, payload.Success ? PortfolioOutcome.Converted : PortfolioOutcome.ConvertFailed,
            payload.OpKey);

    public void Handle(in EventEnvelope envelope, in Enchanted payload) =>
        Count(payload.CharacterId, payload.JobId, payload.ItemNetId, 0, payload.ToLevel > payload.FromLevel ? PortfolioOutcome.EnchantUp
            : payload.ToLevel < payload.FromLevel ? PortfolioOutcome.EnchantDown : PortfolioOutcome.EnchantSame, payload.OpKey);

    /// <summary>
    /// Pins a snapshot of the instance at (<paramref name="at"/>, <paramref name="instanceId"/>) to <paramref name="showcaseSlot"/>
    /// (replacing what was there): only an item the character crafted (its committed properties name it as crafter).
    /// </summary>
    public ContractReason Pin(long characterId, InventoryLocation at, long instanceId, int showcaseSlot, PortfolioAsk ask)
    {
        if (!_inventory.IsAttached(characterId))
            return ContractReason.InventoryUnavailable;
        if (showcaseSlot < 1 || showcaseSlot > _settings.ShowcaseMax)
            return ContractReason.ShowcaseFull;
        var items = _inventory.CurrentInventory(characterId);
        if (instanceId <= 0 || !items.TryIndex(at.Container, out int index) || at.Slot < 0 || at.Slot >= items.BagAt(index).SlotCount)
            return ContractReason.ContractChanged;
        var stack = items.BagAt(index)[at.Slot];
        if (stack.IsEmpty || stack.InstanceId != instanceId || !_inventory.Rules.TryGetItem(stack.ItemNetId, out var def))
            return ContractReason.ContractChanged;
        // Committed properties only (rule 5): a pending enchant is not shown before its save.
        if (!_inventory.TryGetProperties(characterId, instanceId, out var properties) || properties.CrafterId != characterId)
            return ContractReason.PortfolioNotCrafter;
        PendingOf(characterId).Showcase.Add(new ShowcaseWrite(characterId, showcaseSlot, def.Id, stack.Rarity, properties.ToJson(),
            _clock.UtcNowMs));
        Wait(characterId, ask);
        RequestSave?.Invoke(characterId, SaveReason.Class1);
        return ContractReason.None;
    }

    /// <summary>Unpins a showcase slot.</summary>
    public ContractReason Unpin(long characterId, int showcaseSlot, PortfolioAsk ask)
    {
        if (!_inventory.IsAttached(characterId))
            return ContractReason.InventoryUnavailable;
        if (showcaseSlot < 1 || showcaseSlot > _settings.ShowcaseMax)
            return ContractReason.ContractChanged;
        PendingOf(characterId).Showcase.Add(new ShowcaseWrite(characterId, showcaseSlot, "", 0, null, _clock.UtcNowMs));
        Wait(characterId, ask);
        RequestSave?.Invoke(characterId, SaveReason.Class1);
        return ContractReason.None;
    }

    /// <summary>Hides the portfolio from others, or shows it again (R-5.4-04).</summary>
    public ContractReason SetHidden(long characterId, bool hidden, PortfolioAsk ask)
    {
        if (!_inventory.IsAttached(characterId))
            return ContractReason.InventoryUnavailable;
        PendingOf(characterId).Hidden = hidden;
        Wait(characterId, ask);
        RequestSave?.Invoke(characterId, SaveReason.Class1);
        return ContractReason.None;
    }

    /// <summary>Starts a view read off the world thread; false when no reader is set (the answer is then refused at once).</summary>
    public bool View(PortfolioAsk ask)
    {
        if (_read == null)
            return false;
        var read = _read;
        _ = Task.Run(async () =>
        {
            PortfolioRecord? record;
            try
            {
                record = await read(ask.TargetId);
            }
            catch (Exception)
            {
                record = null;
            }
            _answers.Enqueue(new PortfolioAnswer(ask, record));
        });
        return true;
    }

    /// <summary>Phase 1: the reads that came back.</summary>
    public bool TryTakeAnswer(out PortfolioAnswer answer) => _answers.TryDequeue(out answer);

    /// <summary>The pending writes of a character for its save (null when there are none).</summary>
    public PortfolioPending? TakePending(long characterId)
    {
        if (!_pending.Remove(characterId, out var pending))
            return null;
        if (_waitingCommit.Remove(characterId, out var asks))
            _inFlightAsks[characterId] = asks;
        return new PortfolioPending(pending.Counters, pending.Showcase, pending.Hidden);
    }

    /// <summary>A save with the character's portfolio writes committed: <c>PortfolioChanged</c>, and the waiting own views read again.</summary>
    public void Committed(long characterId, PortfolioPending pending)
    {
        Events?.Publish(new PortfolioChanged(characterId), characterId);
        if (_inFlightAsks.Remove(characterId, out var asks))
        {
            foreach (var ask in asks)
            {
                if (!View(ask))
                    _answers.Enqueue(new PortfolioAnswer(ask, null));
            }
        }
    }

    /// <summary>The character's session ended without its save: its pending writes are dropped (class 2 counts may be lost, never doubled).</summary>
    public void Forget(long characterId)
    {
        _pending.Remove(characterId);
        _waitingCommit.Remove(characterId);
        _inFlightAsks.Remove(characterId);
    }

    /// <summary>Converts a record into the wire state for its viewer (a hidden portfolio only for its owner).</summary>
    public PortfolioState StateOf(in PortfolioAsk ask, PortfolioRecord? record, Func<string, int> jobNetIdOf)
    {
        var state = new PortfolioState { RequestId = ask.RequestId, Own = ask.Own };
        if (record == null)
        {
            state.Reason = ContractReason.PortfolioNotFound;
            return state;
        }
        state.Hidden = record.Hidden;
        if (record.Hidden && !ask.Own)
        {
            state.Reason = ContractReason.PortfolioHidden;
            return state;
        }
        state.Counters = record.Counters.Take(ReliableMessages.MaxPortfolioCounters).Select(c => new PortfolioCounter
        {
            JobNetId = (byte)Math.Clamp(jobNetIdOf(c.JobId), 0, byte.MaxValue), Category = (byte)Math.Clamp((int)c.Category, 0, byte.MaxValue),
            Rarity = (byte)Math.Clamp((int)c.Rarity, 0, byte.MaxValue), Outcome = c.Outcome, Count = (uint)Math.Clamp(c.Count, 0, uint.MaxValue),
        }).ToArray();
        state.Showcase = record.Showcase.Where(s => s.Slot >= 1 && s.Slot <= ReliableMessages.MaxShowcase).Select(s => new PortfolioShowcaseEntry
        {
            ShowcaseSlot = (byte)s.Slot, PinnedUtcMs = s.PinnedUtcMs, Item = SnapshotEntry(s),
        }).ToArray();
        return state;
    }

    private InventorySlotEntry SnapshotEntry(ShowcaseRow row)
    {
        var entry = new InventorySlotEntry { Count = 1, Rarity = (byte)Math.Clamp(row.Rarity, 0, byte.MaxValue) };
        if (_inventory.Rules.TryGetItem(row.DefId, out var def))
            entry.ItemNetId = (ushort)def.NetId;
        if (ItemProperties.TryParse(row.ItemJson, out var properties, out _) && _inventory.Rules.Represents(properties))
            Net.InventorySender.Fill(entry, properties, _inventory.Rules);
        return entry;
    }

    private void Count(long characterId, string jobId, int itemNetId, byte rarity, PortfolioOutcome outcome, string opKey)
    {
        if (!_inventory.IsAttached(characterId) || string.IsNullOrEmpty(opKey))
            return; // the crafter left: the count is lost with the class 2 window (never doubled)
        short category = itemNetId != 0 && _inventory.Rules.TryGetItem(itemNetId, out var def) ? (short)((int)def.Category + 1) : (short)0;
        PendingOf(characterId).Counters.Add(new PortfolioIncrement(characterId, jobId, category, rarity, outcome, opKey + ":portfolio",
            _clock.UtcNowMs));
        MarkClass2?.Invoke(characterId);
    }

    private void Wait(long characterId, PortfolioAsk ask)
    {
        if (!_waitingCommit.TryGetValue(characterId, out var asks))
            _waitingCommit.Add(characterId, asks = new List<PortfolioAsk>());
        asks.Add(ask);
    }

    private Pending PendingOf(long characterId)
    {
        if (!_pending.TryGetValue(characterId, out var pending))
            _pending.Add(characterId, pending = new Pending());
        return pending;
    }

    private sealed class Pending
    {
        public List<PortfolioIncrement> Counters { get; } = new();
        public List<ShowcaseWrite> Showcase { get; } = new();
        public bool? Hidden { get; set; }
    }
}
