#nullable enable
using System.Collections.Generic;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;

namespace Game.Gameplay.Entities
{
    /// <summary>
    /// Resource nodes on the client (spec 5.2, WP-M3-4): the node types by net id, and the "in reach" hook the harvest
    /// prompt and the interaction key of WP-M3-5 read (<see cref="NearestNode"/>). Nodes themselves arrive as snapshot
    /// entities of kind <see cref="EntityKind.ResourceNode"/> with the <see cref="EntityFlags.Depleted"/> state bit. The
    /// prompt text is WP-M3-5's. Engine-free (the bots compile it).
    /// </summary>
    public sealed class ClientNodes
    {
        private readonly Dictionary<ushort, NodeDef> _nodes = new Dictionary<ushort, NodeDef>();

        public ClientNodes(ConfigSet config)
        {
            if (!config.TryGet(typeof(NodeDef), out var table))
                return;
            foreach (var row in table.Rows)
            {
                var node = (NodeDef)row;
                _nodes[(ushort)node.NetId] = node;
            }
        }

        /// <summary>The node type of a spawn's definition id; false for an unknown one.</summary>
        public bool TryGetNode(ushort defId, out NodeDef node) => _nodes.TryGetValue(defId, out node!);

        /// <summary>
        /// The node the harvest prompt shows at and the interaction key would harvest: the nearest available node (from the
        /// predicted position) of one of <paramref name="jobs"/> within its <c>NodeDef.reachCm</c>, the range the server
        /// checks, of both the predicted and the last server-confirmed own position (ground cm), like
        /// <see cref="ClientOutposts.NearestNpc"/>; 0 for none. Depleted nodes and other jobs' nodes are skipped. Allocates
        /// nothing (read every frame).
        /// </summary>
        public uint NearestNode(EntityRegistry registry, IOwnJobs jobs, int predictedXCm, int predictedYCm, int confirmedXCm,
            int confirmedYCm)
        {
            uint best = 0;
            long bestSq = long.MaxValue;
            foreach (var entity in registry.EntityValues)
            {
                if (entity.Kind != EntityKind.ResourceNode || (entity.Flags & EntityFlags.Depleted) != 0
                    || !_nodes.TryGetValue(entity.DefId, out var node) || !jobs.Has(node.JobId)
                    || !entity.Samples.TryNewest(out var sample))
                    continue;
                long rangeSq = (long)node.ReachCm * node.ReachCm;
                // Snapshot axes: X is ground X, Z is ground Y (SnapshotAxes).
                long dx = sample.XCm - predictedXCm, dy = sample.ZCm - predictedYCm;
                long sq = dx * dx + dy * dy;
                long cx = sample.XCm - confirmedXCm, cy = sample.ZCm - confirmedYCm;
                if (sq <= rangeSq && cx * cx + cy * cy <= rangeSq && (sq < bestSq || (sq == bestSq && entity.Id < best)))
                {
                    best = entity.Id;
                    bestSq = sq;
                }
            }
            return best;
        }
    }
}
