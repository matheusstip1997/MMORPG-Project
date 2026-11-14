using GameServer.Events;
using GameServer.Persistence;

namespace GameServer.Economy;

/// <summary>
/// Faucet and sink totals of one world (R-4.2-07, spec 7.2 economy snapshots; WP-M2-4). Its sink side is the spec's
/// <c>SinkLedger</c>: every destruction counted by reason (gold from <see cref="GoldDestroyed"/>, items from
/// <see cref="ItemDestroyed"/>); the faucet side counts gold created (<see cref="GoldChanged"/> whose flow is a faucet,
/// <see cref="EconomyFlows.Of"/>: gold moved between owners is neither, R-4.5-02). Every change is also a <c>gold_ledger</c>
/// or <c>audit_log</c> row in the database; these running totals since the world started feed the periodic
/// <c>EconomyTotals</c> and <c>EconomyByReason</c> telemetry and the <c>faucet_sink_snapshots</c> rows (WP-M4-5). Phase 11,
/// idempotent per event id; read on the world thread.
/// </summary>
public sealed class EconomyLedger : IEventConsumer<GoldChanged>, IEventConsumer<GoldDestroyed>, IEventConsumer<ItemDestroyed>,
    IEventConsumer<MailExpired>, IEventConsumer<LootBagExpired>
{
    /// <summary>
    /// The <see cref="InventoryReason"/> values counted: every value up to the enum's highest, so a reason a later WP appends
    /// counts without another edit here (WP-M4-1; before it the count stopped at <c>Enchant</c>).
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
        bus.Subscribe<LootBagExpired>(this); // WP-M4-9: an expired loot bag's items are destroyed
    }

    public long GoldCreatedBy(InventoryReason reason) => At(_goldCreated, reason);
    public long GoldDestroyedBy(InventoryReason reason) => At(_goldDestroyed, reason);
    public long ItemsDestroyedBy(InventoryReason reason) => At(_itemsDestroyed, reason);

    public void Handle(in EventEnvelope envelope, in GoldChanged payload)
    {
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

    /// <summary>
    /// An expired mail's gold and units were destroyed (spec 4.5 M4 mail, a sink; WP-M4-3). An admin compensation is left out: its
    /// gold and items are created only by the claim (reason <c>Admin</c>, the faucet), so an unclaimed one never entered the
    /// economy and its expiry destroys nothing that was counted.
    /// </summary>
    public void Handle(in EventEnvelope envelope, in MailExpired payload)
    {
        if (!_watermark.TryAccept(envelope) || payload.Kind == Shared.Protocol.MailKind.AdminCompensation)
            return;
        if (payload.Gold > 0)
        {
            GoldDestroyed += payload.Gold;
            Add(_goldDestroyed, InventoryReason.MailExpired, payload.Gold);
        }
        foreach (var item in payload.Items)
            Add(_itemsDestroyed, InventoryReason.MailExpired, item.Count);
    }

    /// <summary>An expired loot bag's units were destroyed (spec 4.3 R-4.3-03, a sink; WP-M4-9).</summary>
    public void Handle(in EventEnvelope envelope, in LootBagExpired payload)
    {
        if (_watermark.TryAccept(envelope))
            Add(_itemsDestroyed, InventoryReason.BagExpired, payload.Units);
    }

    private static long At(long[] totals, InventoryReason reason) => (int)reason < totals.Length ? totals[(int)reason] : 0;

    private static void Add(long[] totals, InventoryReason reason, long amount)
    {
        if ((int)reason < totals.Length)
            totals[(int)reason] += amount;
    }
}
