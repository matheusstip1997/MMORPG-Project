using Shared.Protocol;

namespace GameServer.Items;

/// <summary>Where a use stands between its request and its effect point (spec 3.4 consumable use, WP-M3-3).</summary>
internal enum PendingUseState : byte
{
    /// <summary>Checked in phase 1; the action starts (or is refused) in phase 3.</summary>
    Requested = 0,
    /// <summary>The use action runs; nothing is used until its consume effect fires.</summary>
    Running = 1,
}

/// <summary>A use asked for and not yet at its effect point: the slot it names and the consumable that was there.</summary>
internal sealed class PendingUse
{
    public PendingUse(uint requestId, string opKey, byte container, byte slot, ConsumableEntry entry)
    {
        RequestId = requestId;
        OpKey = opKey;
        Container = container;
        Slot = slot;
        Entry = entry;
    }

    public uint RequestId { get; }
    public string OpKey { get; }

    /// <summary><c>ContainerDef.code</c> (the Normal bag's for a request's 0).</summary>
    public byte Container { get; }
    public byte Slot { get; }
    public ConsumableEntry Entry { get; }
    public PendingUseState State { get; set; }
}

/// <summary>A used item waiting for the save that holds its removal (rule 5: confirm only after the commit).</summary>
internal readonly record struct PendingUseConfirmation(string OpKey, UseItemResult Result);

/// <summary>
/// One player's item-use traffic (spec 3.4 consumable use, WP-M3-3): the use in progress (at most one), refusals for phase
/// 10, uses waiting for their commit, and the cooldown of each consumable (volatile like statuses: a relogin starts with
/// none). World thread only.
/// </summary>
internal sealed class UseItemAnswers
{
    private uint[] _readyTick = Array.Empty<uint>();
    private readonly Dictionary<string, ushort> _usedItems = new(StringComparer.Ordinal);

    /// <summary>The use between its request and its effect point; null when none.</summary>
    public PendingUse? Current { get; set; }

    /// <summary>Refusals and interruptions waiting for phase 10, oldest first.</summary>
    public List<UseItemResult> Refusals { get; } = new();

    /// <summary>Used items waiting for their save, oldest first.</summary>
    public List<PendingUseConfirmation> Confirmations { get; } = new();

    /// <summary>The first tick a consumable (<see cref="ConsumableEntry.Index"/>) may be used again; 0 when it never was.</summary>
    public uint ReadyTick(int index) => index < _readyTick.Length ? _readyTick[index] : 0;

    public void StartCooldown(int index, int catalogCount, uint readyTick)
    {
        if (_readyTick.Length < catalogCount)
            Array.Resize(ref _readyTick, catalogCount);
        _readyTick[index] = readyTick;
    }

    /// <summary>The item an applied operation key used (a repeat of the request is answered with it).</summary>
    public ushort UsedItem(string opKey) => _usedItems.TryGetValue(opKey, out var netId) ? netId : (ushort)0;

    public void RecordUsed(string opKey, ushort itemNetId) => _usedItems[opKey] = itemNetId;

    /// <summary>
    /// A new connection or none (bind, unbind): answers nobody will read are dropped, and a use not yet at its effect point
    /// uses nothing (its action may run on; the effect then finds no use). The cooldowns stay with the character.
    /// </summary>
    public void Reset()
    {
        Current = null;
        Refusals.Clear();
        Confirmations.Clear();
        _usedItems.Clear(); // operation keys carry the connection, so the old ones never repeat
    }

    /// <summary>A disconnected player: answers nobody will read.</summary>
    public void ClearAnswers()
    {
        Refusals.Clear();
        Confirmations.Clear();
    }
}
