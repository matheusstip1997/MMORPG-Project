using Shared.Data.Tables;
using Shared.Simulation.Actions;
using Shared.Simulation.Gathering;

namespace GameServer.World.Nodes;

/// <summary>
/// One harvestable node type in runtime form (spec 5.2, WP-M3-5): its <see cref="NodeDef"/>, its harvest action as a catalog
/// index and its yield table in file order.
/// </summary>
internal sealed class HarvestNodeEntry
{
    public HarvestNodeEntry(NodeDef def, ActionTiming action, HarvestChoice[] table)
    {
        Def = def;
        ActionIndex = action.Index;
        Table = table;
    }

    public NodeDef Def { get; }

    /// <summary>The harvest action's index in the world's <see cref="ActionCatalog"/> (every player's machine runs on it).</summary>
    public int ActionIndex { get; }

    public HarvestChoice[] Table { get; }
}

/// <summary>
/// Every harvestable node type by <see cref="NodeDef.NetId"/> (spec 5.2, WP-M3-5), built once from the validated tables: a
/// node whose action is not a harvest action or whose yield table is empty is left out (the validator reports it), and a
/// request for it is answered <c>NodeNotFound</c>. Immutable.
/// </summary>
internal sealed class HarvestCatalog
{
    private readonly Dictionary<int, HarvestNodeEntry> _byNodeNetId = new();

    private HarvestCatalog()
    {
    }

    public int Count => _byNodeNetId.Count;

    public bool TryGet(int nodeNetId, out HarvestNodeEntry entry) => _byNodeNetId.TryGetValue(nodeNetId, out entry!);

    public static HarvestCatalog From(NodeRules nodes, ActionCatalog actions)
    {
        var catalog = new HarvestCatalog();
        foreach (var def in nodes.Nodes)
        {
            if (actions.Find(def.ActionId) is not { Kind: ActionKind.Harvest } action
                || !nodes.YieldTables.TryGetValue(def.YieldTableId, out var table) || table.Length == 0)
                continue;
            catalog._byNodeNetId[def.NetId] = new HarvestNodeEntry(def, action, table);
        }
        return catalog;
    }
}
