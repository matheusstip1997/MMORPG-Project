using Shared.Data.Tables;
using Shared.Simulation.Actions;
using Shared.Simulation.Stats;

namespace GameServer.Combat;

/// <summary>
/// One active status on an entity (spec 3.7 <c>{effectId, sourceId, expiresAt | stacks}</c>), in ticks. Its stat changes
/// are in the entity's <see cref="StatBlock"/> under <see cref="InstanceId"/> as source id.
/// </summary>
internal struct StatusInstance
{
    public StatusDefinition Definition;

    /// <summary>World-unique; the source id of its <see cref="StatModifier"/>s.</summary>
    public long InstanceId;

    /// <summary>
    /// World-wide order of its newest application (first or refresh): cleanse and strip remove the highest first (spec 3.7
    /// M2 status list, WP-M2-9).
    /// </summary>
    public long AppliedSeq;

    public int Stacks;

    public uint AppliedTick;

    /// <summary>Removed in phase 6 of this tick: active through phase 5 of it.</summary>
    public uint EndTick;

    /// <summary>The next periodic tick (damage or heal); meaningless without ticks.</summary>
    public uint NextTickAt;

    /// <summary>A burst (WP-M2-9) waits for the next phase 6: set when the status is applied or refreshed.</summary>
    public bool BurstPending;

    /// <summary>
    /// WP-M2-9 <c>endsWithPhase</c>: the catalog index of the caster's own action that holds it, or -1; it ends when that
    /// action is no longer in <see cref="BoundPhase"/>.
    /// </summary>
    public int BoundActionIndex;

    public ActionState BoundPhase;

    /// <summary>Who applied it last (credit, R-3.8-09): entity id and character id (0 for monsters).</summary>
    public uint SourceEntityId;

    public long SourceCharacterId;

    /// <summary>
    /// The applier's side of the tick and burst damage, captured when applied (power, penetration, rank, damage dealt, the
    /// Bystial bonus against the target), so it keeps ticking after the applier dies or leaves; or, for a tick heal, the
    /// applier's Matk in <see cref="DamageInput.Power"/>.
    /// </summary>
    public DamageInput Captured;
}

/// <summary>
/// The active statuses of one combatant (spec 3.7 <c>StatusSystem</c> state), in the order they were first applied (the
/// buff row order on the wire). One instance per status (user decision 2026-10-23). Only <see cref="StatusSystem"/>
/// changes it. Grows when full and never shrinks, so steady state allocates nothing. World simulation thread only.
/// </summary>
internal sealed class StatusSet
{
    private StatusInstance[] _items = new StatusInstance[4];

    public int Count { get; private set; }

    public ref StatusInstance this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count)
                throw new ArgumentOutOfRangeException(nameof(index));
            return ref _items[index];
        }
    }

    /// <summary>Index of the instance of <paramref name="definition"/>, or -1.</summary>
    public int IndexOf(StatusDefinition definition)
    {
        for (int i = 0; i < Count; i++)
        {
            if (ReferenceEquals(_items[i].Definition, definition))
                return i;
        }
        return -1;
    }

    /// <summary>True when the status with this id is active.</summary>
    public bool Has(string statusId)
    {
        for (int i = 0; i < Count; i++)
        {
            if (_items[i].Definition.Id == statusId)
                return true;
        }
        return false;
    }

    /// <summary>Index of the first active status with this control (WP-M2-9: stun, blind, provoke), or -1. No string compares.</summary>
    public int IndexOf(StatusControl control)
    {
        for (int i = 0; i < Count; i++)
        {
            if (_items[i].Definition.Control == control)
                return i;
        }
        return -1;
    }

    /// <summary>True when a status with this control is active (WP-M2-9).</summary>
    public bool Has(StatusControl control) => IndexOf(control) >= 0;

    public void Add(in StatusInstance instance)
    {
        if (Count == _items.Length)
            Array.Resize(ref _items, _items.Length * 2);
        _items[Count++] = instance;
    }

    /// <summary>Removes one instance, keeping the order of the others.</summary>
    public void RemoveAt(int index)
    {
        if ((uint)index >= (uint)Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        Count--;
        if (index < Count)
            Array.Copy(_items, index + 1, _items, index, Count - index);
        _items[Count] = default;
    }
}
