using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;

namespace GameServer.Entities;

/// <summary>
/// An outpost or vendor NPC (spec 4.2, WP-M2-4), placed at world start by an exported <c>npc</c> marker. It is an entity
/// in the snapshot (its spawn event carries <c>NpcDef.netId</c>, so clients draw <see cref="NpcDef.VisualId"/>), never a
/// combatant (no <see cref="Entity.Combatant"/>: hits, statuses, the monster AI and body blocking skip it) and never a
/// lock target (the client locks monsters only). Players interact with it through <c>OutpostInteractions</c>.
/// </summary>
public sealed class Npc : Entity
{
    public Npc(uint id, NpcDef def, string markerId, MovementState movement, int heightCm)
        : base(id, EntityKind.Npc, movement, heightCm)
    {
        Def = def;
        MarkerId = markerId;
        DefId = (ushort)def.NetId;
    }

    public NpcDef Def { get; }

    /// <summary>The npc marker it stands on.</summary>
    public string MarkerId { get; }
}
