using GameServer.Combat;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;

namespace GameServer.Entities;

/// <summary>
/// A monster (backlog F) of one <see cref="MonsterDef"/>, spawned at a <see cref="SpawnPoints"/> marker by the world's
/// <c>SpawnController</c>. Always has a <see cref="Entity.Combatant"/>. No AI yet: it stands where it spawned (TODO(F)).
/// </summary>
public sealed class Monster : Entity
{
    public Monster(uint id, MonsterDef def, string spawnPointId, MovementState movement, int heightCm, Combatant combatant)
        : base(id, EntityKind.Monster, movement, heightCm)
    {
        Def = def;
        SpawnPointId = spawnPointId;
        Combatant = combatant;
        DefId = (ushort)def.NetId;
    }

    public MonsterDef Def { get; }

    /// <summary>The <see cref="SpawnPoints"/> marker it belongs to; it respawns there.</summary>
    public string SpawnPointId { get; }
}
