using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;

namespace GameServer.Entities;

/// <summary>Where a resource node is in its cycle (spec 5.2 R-5.2-03).</summary>
public enum NodeState : byte
{
    /// <summary>It can be harvested.</summary>
    Available = 0,

    /// <summary>
    /// Harvested and waiting for <see cref="ResourceNode.RespawnAtMs"/> (the spec's "depleted" and "respawning": a node is
    /// never depleted without a scheduled respawn).
    /// </summary>
    Depleted = 1,
}

/// <summary>
/// A resource node (spec 5.2, WP-M3-4), placed at world start on a resourceNode marker by its <see cref="GatherNodes"/> row.
/// It is an entity in the snapshot (its spawn event carries <c>NodeDef.netId</c>; a depleted node carries the
/// <see cref="EntityFlags.Depleted"/> flag), visible to everyone like an NPC (rule 9: it is not hidden), never a combatant
/// (no <see cref="Entity.Combatant"/>: hits, statuses, the monster AI and body blocking skip it) and never a lock target
/// (the client locks monsters only). Only the world's <c>NodeController</c> changes its state; nodes live in memory, so
/// every node is available after a restart.
/// </summary>
public sealed class ResourceNode : Entity
{
    public ResourceNode(uint id, NodeDef def, string markerId, MovementState movement, int heightCm)
        : base(id, EntityKind.ResourceNode, movement, heightCm)
    {
        Def = def;
        MarkerId = markerId;
        DefId = (ushort)def.NetId;
    }

    public NodeDef Def { get; }

    /// <summary>The resourceNode marker it stands on.</summary>
    public string MarkerId { get; }

    public NodeState State { get; internal set; }

    /// <summary>World time (ms, <c>GameWorld.NowMs</c>) at which a depleted node becomes available again; 0 while available.</summary>
    public long RespawnAtMs { get; internal set; }

    public bool IsAvailable => State == NodeState.Available;
}
