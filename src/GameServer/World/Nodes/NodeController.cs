using GameServer.Entities;
using GameServer.World.Map;
using Shared.Simulation;
using Shared.Simulation.Economy;

namespace GameServer.World.Nodes;

/// <summary>What the node controller reads from the demand loop (R-5.2-03, R-4.2-03; <c>DemandService</c>).</summary>
public interface INodeDemandQuery
{
    /// <summary>Respawn delay scale of a node type in a region, whole percent (100 = the base delay).</summary>
    int NodeSpawnDelayPercent(int nodeNetId, byte regionIndex);
}

/// <summary>
/// The world's resource nodes (spec 5.2 R-5.2-03, WP-M3-4): one <see cref="ResourceNode"/> per <see cref="NodeRules.Placement"/>
/// row at its resourceNode marker, created when the world is built and never removed. A harvest depletes a node
/// (<see cref="TryDeplete"/>, called by harvesting, WP-M3-5, after its commit) and schedules its respawn on the world
/// clock: <c>NodeDef.respawnMs</c> scaled by the demand loop's spawn delay percent of the node type in the node's region,
/// never below <c>GatherSettings.minRespawnMs</c> (or the base delay when that is shorter) and never zero
/// (<see cref="RespawnMath.ScaledDelay"/>). Phase 8 brings back every node whose time has come. Nodes are handled in
/// marker id order. Simulation thread only; allocates only at construction.
/// </summary>
public sealed class NodeController
{
    private readonly ResourceNode[] _nodes;
    private INodeDemandQuery? _demand;
    private readonly int _minRespawnMs;

    /// <param name="nextEntityId">Hands out the entity id of each node, in marker id order.</param>
    /// <exception cref="ArgumentException">A row's marker or node type is missing (the loaders report these first).</exception>
    public NodeController(NodeRules rules, WorldMap map, Func<uint> nextEntityId)
    {
        var errors = WorldLoader.CheckGatherNodes(map, rules.Placement);
        if (errors.Count > 0)
            throw new ArgumentException(errors[0].Message, nameof(map));
        Rules = rules;
        _nodes = new ResourceNode[rules.Placement.Count];
        for (int i = 0; i < _nodes.Length; i++)
        {
            var row = rules.Placement[i];
            if (!rules.TryGetNode(row.NodeId, out var def))
                throw new ArgumentException($"GatherNodes '{row.MarkerId}' names unknown node '{row.NodeId}'", nameof(rules));
            map.TryGetMarker(row.MarkerId, out var marker);
            _nodes[i] = new ResourceNode(nextEntityId(), def, row.MarkerId, new MovementState(marker.Position, marker.Facing),
                map.HeightCmAt(marker.Position, map.Grid.BaseHeightCm))
            {
                RegionIndex = map.Grid.RegionIndexAt(marker.Position),
                SpeedCheckedPosition = marker.Position,
            };
        }
        _minRespawnMs = rules.Settings.MinRespawnMs;
    }

    public NodeRules Rules { get; }

    /// <summary>The nodes, in marker id order (their entity ids rise in the same order).</summary>
    public IReadOnlyList<ResourceNode> Nodes => _nodes;

    /// <summary>
    /// The demand loop (R-5.2-03): each respawn delay is scaled by its node type's spawn delay percent in the node's
    /// region. Without it delays stay at their base.
    /// </summary>
    public void SetDemand(INodeDemandQuery demand) => _demand = demand;

    /// <summary>The node with this entity id (harvesting looks its target up here, WP-M3-5).</summary>
    public bool TryGet(uint entityId, out ResourceNode node)
    {
        int low = 0, high = _nodes.Length - 1;
        while (low <= high)
        {
            int mid = (low + high) >> 1;
            uint id = _nodes[mid].Id;
            if (id == entityId)
            {
                node = _nodes[mid];
                return true;
            }
            if (id < entityId)
                low = mid + 1;
            else
                high = mid - 1;
        }
        node = null!;
        return false;
    }

    /// <summary>The delay a node depleted now would wait, in ms, after the demand scale (R-5.2-03).</summary>
    public long RespawnDelayMs(ResourceNode node)
    {
        int percent = _demand?.NodeSpawnDelayPercent(node.Def.NetId, node.RegionIndex) ?? 100;
        return RespawnMath.ScaledDelay(node.Def.RespawnMs, percent, _minRespawnMs);
    }

    /// <summary>
    /// One harvest depletes an available node (decision 5) and schedules its respawn from <paramref name="nowMs"/>. False
    /// when the entity is not a node of this world or the node is already depleted (harvesting refuses with
    /// <c>NodeDepleted</c>, WP-M3-5).
    /// </summary>
    public bool TryDeplete(uint entityId, long nowMs)
    {
        if (!TryGet(entityId, out var node) || !node.IsAvailable)
            return false;
        node.State = NodeState.Depleted;
        node.RespawnAtMs = nowMs + RespawnDelayMs(node);
        return true;
    }

    /// <summary>
    /// The dev console's <c>/node respawn</c> (WP-M3-13): a depleted node is available again at once, as when its respawn
    /// time comes. False when it is available already.
    /// </summary>
    public bool RespawnNow(ResourceNode node)
    {
        if (node.State != NodeState.Depleted)
            return false;
        node.State = NodeState.Available;
        node.RespawnAtMs = 0;
        return true;
    }

    /// <summary>Phase 8: every depleted node whose respawn time has come is available again.</summary>
    public void RespawnDue(long nowMs)
    {
        foreach (var node in _nodes)
        {
            if (node.State == NodeState.Depleted && nowMs >= node.RespawnAtMs)
            {
                node.State = NodeState.Available;
                node.RespawnAtMs = 0;
            }
        }
    }
}
