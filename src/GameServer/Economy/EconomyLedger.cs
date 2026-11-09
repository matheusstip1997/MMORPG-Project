using GameServer.Persistence;
using GameServer.Events;

namespace GameServer.Economy;

/// <summary>
/// Faucet and sink totals of one world (R-4.2-07, spec 7.2 economy snapshots; WP-M2-4). Its sink side is the spec's
/// <c>SinkLedger</c>: every destruction counted by reason (gold from <see cref="GoldDestroyed"/>, items from
/// <see cref="ItemDestroyed"/>); the faucet side counts gold created (<see cref="GoldChanged"/> with a positive delta).
/// Every change is also a <c>gold_ledger</c> or <c>audit_log</c> row in the database; these running totals since the
/// world started feed the periodic <c>EconomyTotals</c> and <c>EconomyByReason</c> telemetry. Phase 11, idempotent per
/// event id; read on the world thread.
/// </summary>
public sealed class EconomyLedger : IEventConsumer<GoldChanged>, IEventConsumer<GoldDestroyed>, IEventConsumer<ItemDestroyed>,
    IEventConsumer<MailExpired>
{
    /// <summary>
    /// One past the highest <see cref="InventoryReason"/> value: every reason is counted, whatever the enum grows to (WP-M4-3; it
    /// stopped at <c>Enchant</c> before, so the gem reasons were not counted).
    /// </summary>
    public static readonly int ReasonCount = Enum.GetValues<InventoryReason>().Max(r => (int)r) + 1;

    private readonly long[] _goldCreated = new long[ReasonCount];
    private readonly long[] _goldDestroyed = new long[ReasonCount];
    private readonly long[] _itemsDestroyed = new long[ReasonCount];
    private EventWatermark _watermark;

    public long GoldCreated { get; private set; }
    public long GoldDestroyed { get; private set; }

    public void Subscribe(EventBus bus)
    {
        bus.Subscribe<GoldChanged>(this);
        bus.Subscribe<GoldDestroyed>(this);
        bus.Subscribe<ItemDestroyed>(this);
        bus.Subscribe<MailExpired>(this); // WP-M4-3: expired attachments are destroyed
    }

    public long GoldCreatedBy(InventoryReason reason) => At(_goldCreated, reason);
    public long GoldDestroyedBy(InventoryReason reason) => At(_goldDestroyed, reason);
    public long ItemsDestroyedBy(InventoryReason reason) => At(_itemsDestroyed, reason);

    public void Handle(in EventEnvelope envelope, in GoldChanged payload)
    {
        // Only faucets create gold: a move (a mail claim, a trade) only changes hands (EconomyFlows, WP-M4-3).
        if (!_watermark.TryAccept(envelope) || payload.Delta <= 0 || EconomyFlows.Of(payload.Reason, payload.Delta) != GoldFlow.Faucet)
            return;
        GoldCreated += payload.Delta;
        Add(_goldCreated, payload.Reason, payload.Delta);
    }

    public void Handle(in EventEnvelope envelope, in GoldDestroyed payload)
    {
        if (!_watermark.TryAccept(envelope))
            return;
        GoldDestroyed += payload.Amount;
        Add(_goldDestroyed, payload.Reason, payload.Amount);
    }

    public void Handle(in EventEnvelope envelope, in ItemDestroyed payload)
    {
        if (_watermark.TryAccept(envelope))
            Add(_itemsDestroyed, payload.Reason, payload.Count);
    }

    /// <summary>An expired mail's gold and units were destroyed (spec 4.5 M4 mail, a sink; WP-M4-3).</summary>
    public void Handle(in EventEnvelope envelope, in MailExpired payload)
    {
        if (!_watermark.TryAccept(envelope))
            return;
        if (payload.Gold > 0)
        {
            GoldDestroyed += payload.Gold;
            Add(_goldDestroyed, InventoryReason.MailExpired, payload.Gold);
        }
        foreach (var item in payload.Items)
            Add(_itemsDestroyed, InventoryReason.MailExpired, item.Count);
    }

    private static long At(long[] totals, InventoryReason reason) => (int)reason < totals.Length ? totals[(int)reason] : 0;

    private static void Add(long[] totals, InventoryReason reason, long amount)
    {
        if ((int)reason < totals.Length)
            totals[(int)reason] += amount;
    }
}
