using GameServer.Combat;
using Shared.Data.Tables;
using Shared.Simulation.Actions;

namespace GameServer.Items;

/// <summary>
/// One usable consumable in runtime form (spec 3.4 consumable use, WP-M3-3): its <see cref="ConsumableDef"/> row with the
/// item's net id, the action it plays as a catalog index, its status and its cooldown in ticks.
/// </summary>
internal sealed class ConsumableEntry
{
    public ConsumableEntry(int index, ConsumableDef def, ushort itemNetId, ActionTiming action, StatusDefinition? status,
        int cooldownTicks)
    {
        Index = index;
        Def = def;
        ItemNetId = itemNetId;
        ActionIndex = action.Index;
        ActionNetId = action.NetId;
        Status = status;
        CooldownTicks = cooldownTicks;
    }

    /// <summary>Position in the catalog (the per-player cooldown slot).</summary>
    public int Index { get; }

    public ConsumableDef Def { get; }

    public ushort ItemNetId { get; }

    /// <summary>The use action's index in the world's <see cref="ActionCatalog"/> (every player's machine runs on it).</summary>
    public int ActionIndex { get; }

    public int ActionNetId { get; }

    /// <summary>The status applied at the effect point (source potion or food), or null.</summary>
    public StatusDefinition? Status { get; }

    /// <summary><c>cooldownMs</c> rounded up to whole ticks; 0 for none.</summary>
    public int CooldownTicks { get; }
}

/// <summary>
/// Every usable consumable by item net id (spec 3.4 consumable use, WP-M3-3), built once from the validated tables: a row
/// whose item, action or status is missing is left out (the validator reports it). Immutable.
/// </summary>
internal sealed class ConsumableCatalog
{
    private readonly Dictionary<int, ConsumableEntry> _byItemNetId = new();

    private ConsumableCatalog()
    {
    }

    public static readonly ConsumableCatalog Empty = new();

    public int Count => _byItemNetId.Count;

    public bool TryGet(int itemNetId, out ConsumableEntry entry) => _byItemNetId.TryGetValue(itemNetId, out entry!);

    public static ConsumableCatalog From(CombatRules combat, ActionCatalog actions, ItemRules items)
    {
        var catalog = new ConsumableCatalog();
        foreach (var row in combat.Consumables)
        {
            if (!items.TryGetItem(row.Id, out var item) || actions.Find(row.ActionId) is not { Kind: ActionKind.UseItem } action)
                continue;
            StatusDefinition? status = null;
            if (row.StatusId.Length != 0 && !combat.Statuses.TryGet(row.StatusId, out status))
                continue;
            catalog._byItemNetId[item.NetId] = new ConsumableEntry(catalog._byItemNetId.Count, row, (ushort)item.NetId, action,
                status, combat.TicksFor(row.CooldownMs));
        }
        return catalog;
    }
}
