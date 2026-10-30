using Shared.Data;
using Shared.Data.Tables;

namespace GameServer.World.Nodes;

/// <summary>
/// The resource node tables (spec 5.2, WP-M3-4): node types by id and net id, the <see cref="GatherNodes"/> placement in
/// marker id order (rule 6: a fixed order, so entity ids are the same every start) and <see cref="GatherSettings"/>.
/// Built once at start from tables that passed validation (the node tables reload only with a restart).
/// </summary>
public sealed class NodeRules
{
    private readonly Dictionary<string, NodeDef> _byId;
    private readonly Dictionary<int, NodeDef> _byNetId;

    public NodeRules(IReadOnlyList<NodeDef> nodes, IReadOnlyList<GatherNodes> placement, GatherSettings settings)
    {
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

    public bool TryGetNode(string id, out NodeDef node) => _byId.TryGetValue(id, out node!);

    public bool TryGetNode(int netId, out NodeDef node) => _byNetId.TryGetValue(netId, out node!);

    /// <summary>From a loaded config set; throws when a node table is missing.</summary>
    public static NodeRules From(ConfigSet set) =>
        new(Rows<NodeDef>(set), Rows<GatherNodes>(set), set.Single<GatherSettings>());

    private static List<T> Rows<T>(ConfigSet set) where T : class =>
        set.TryGet(typeof(T), out var table)
            ? table.Rows.Cast<T>().ToList()
            : throw new InvalidOperationException($"Table {typeof(T).Name} is not loaded (spec 5.2 resource nodes).");
}
