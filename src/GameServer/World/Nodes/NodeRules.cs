using Shared.Data;
using Shared.Data.Tables;
using Shared.Simulation.Gathering;

namespace GameServer.World.Nodes;

/// <summary>
/// The resource node tables (spec 5.2, WP-M3-4): node types by id and net id, the <see cref="GatherNodes"/> placement in
/// marker id order (rule 6: a fixed order, so entity ids are the same every start) and <see cref="GatherSettings"/>; for
/// harvesting (WP-M3-5) the <see cref="NodeYieldEntry"/> rows and <see cref="HarvestSettings"/> (null without them: nothing
/// can be harvested). Built once at start from tables that passed validation (the node tables reload only with a restart).
/// </summary>
public sealed class NodeRules
{
    private readonly Dictionary<string, NodeDef> _byId;
    private readonly Dictionary<int, NodeDef> _byNetId;

    public NodeRules(IReadOnlyList<NodeDef> nodes, IReadOnlyList<GatherNodes> placement, GatherSettings settings,
        IReadOnlyDictionary<string, HarvestChoice[]>? yieldTables = null, HarvestSettings? harvest = null)
    {
        YieldTables = yieldTables ?? new Dictionary<string, HarvestChoice[]>(StringComparer.Ordinal);
        Harvest = harvest;
        _byId = nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        _byNetId = nodes.ToDictionary(n => n.NetId);
        Nodes = nodes;
        Placement = placement.OrderBy(p => p.MarkerId, StringComparer.Ordinal).ToArray();
        Settings = settings;
    }

    public IReadOnlyList<NodeDef> Nodes { get; }

    /// <summary>One row per node in the world, in marker id order.</summary>
    public IReadOnlyList<GatherNodes> Placement { get; }

    public GatherSettings Settings { get; }

    /// <summary>The yield tables by <c>NodeDef.yieldTableId</c>, each in file order (the order a roll weighs them, rule 6).</summary>
    public IReadOnlyDictionary<string, HarvestChoice[]> YieldTables { get; }

    /// <summary>The yield rules (WP-M3-5); null in a world without them, where harvesting is off.</summary>
    public HarvestSettings? Harvest { get; }

    public bool TryGetNode(string id, out NodeDef node) => _byId.TryGetValue(id, out node!);

    public bool TryGetNode(int netId, out NodeDef node) => _byNetId.TryGetValue(netId, out node!);

    /// <summary>From a loaded config set; throws when a node table is missing.</summary>
    public static NodeRules From(ConfigSet set) =>
        new(Rows<NodeDef>(set), Rows<GatherNodes>(set), set.Single<GatherSettings>(),
            YieldTablesFrom(set), HarvestSettings.Find(set));

    /// <summary>The <see cref="NodeYieldEntry"/> rows grouped by table, with item net ids and rarity tiers (empty without the table).</summary>
    private static Dictionary<string, HarvestChoice[]> YieldTablesFrom(ConfigSet set)
    {
        var tables = new Dictionary<string, List<HarvestChoice>>(StringComparer.Ordinal);
        if (set.TryGet(typeof(NodeYieldEntry), out var entries))
        {
            foreach (NodeYieldEntry entry in entries.Rows)
            {
                var item = ItemDef.Find(set, entry.ItemId)
                           ?? throw new InvalidOperationException($"NodeYieldEntry {entry.Id}: no ItemDef {entry.ItemId}");
                var rarity = Rows<RarityDef>(set).FirstOrDefault(r => r.Id == entry.RarityId)
                             ?? throw new InvalidOperationException($"NodeYieldEntry {entry.Id}: no RarityDef {entry.RarityId}");
                if (!tables.TryGetValue(entry.TableId, out var list))
                    tables[entry.TableId] = list = new List<HarvestChoice>();
                // A gem find (decision 13): the job tree's gem-find bonus raises the chance of these entries.
                bool isGem = Array.IndexOf(item.MaterialTags, "roughGem") >= 0 || Array.IndexOf(item.MaterialTags, "gem") >= 0;
                list.Add(new HarvestChoice((ushort)item.NetId, (byte)rarity.Tier, entry.Weight, entry.CountMin, entry.CountMax,
                    entry.MinJobLevel, isGem));
            }
        }
        return tables.ToDictionary(t => t.Key, t => t.Value.ToArray(), StringComparer.Ordinal);
    }

    private static List<T> Rows<T>(ConfigSet set) where T : class =>
        set.TryGet(typeof(T), out var table)
            ? table.Rows.Cast<T>().ToList()
            : throw new InvalidOperationException($"Table {typeof(T).Name} is not loaded (spec 5.2 resource nodes).");
}
