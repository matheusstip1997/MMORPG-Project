using GameServer.Entities;
using GameServer.World.Map;
using GameServer.World.Nodes;

namespace GameServer.World;

/// <summary>
/// Resource nodes in the world (spec 5.2 R-5.2-03, WP-M3-4): the <see cref="NodeController"/>'s nodes are entities from
/// the start (in the grid, so every observer in range sees them through the visibility query, rule 9), their respawns
/// run in phase 8 on the world clock (<see cref="NowMs"/>), and the demand loop scales their delays. Harvesting (WP-M3-5)
/// depletes a node through <see cref="TryDepleteNode"/> once its harvest committed.
/// </summary>
public sealed partial class GameWorld
{
    private NodeController? _nodes;

    /// <summary>The world's resource nodes; null when the world was built without the node tables or the combat rules.</summary>
    public NodeController? Nodes => _nodes;

    /// <summary>One node entity per <c>GatherNodes</c> row, after the NPCs, in marker id order (rule 6).</summary>
    private void BuildNodes(NodeRules nodes, WorldMap map)
    {
        _nodes = new NodeController(nodes, map, () => _nextEntityId++);
        foreach (var node in _nodes.Nodes)
        {
            _entities.Add(node);
            _grid.Add(node);
        }
        if (Demand != null)
            _nodes.SetDemand(Demand);
    }

    /// <summary>
    /// Depletes an available resource node now and schedules its respawn (decision 5: one harvest depletes it). The hook
    /// harvesting (WP-M3-5) calls after its harvest committed, and the stand-in for it in tests until then. False when
    /// <paramref name="nodeEntityId"/> is not a node or the node is depleted. Simulation thread only.
    /// </summary>
    internal bool TryDepleteNode(uint nodeEntityId) => _nodes != null && _nodes.TryDeplete(nodeEntityId, NowMs);
}
