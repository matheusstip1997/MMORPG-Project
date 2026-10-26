using GameServer.AI;
using GameServer.Combat;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;

namespace GameServer.Entities;

/// <summary>
/// A monster (backlog F) of one <see cref="MonsterDef"/>, spawned at a <see cref="SpawnPoints"/> marker by the world's
/// <c>SpawnController</c>. Always has a <see cref="Entity.Combatant"/>. Its <see cref="Brain"/> is driven by the generic
/// monster AI (<see cref="MonsterAi"/>): aggro, chase, attack, leash home to where it spawned.
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
        Brain = new MonsterBrain(movement.Position, movement.Facing);
    }

    /// <summary>Its row; replaced by a dev hot reload with the reloaded row of the same id (spec 11.7).</summary>
    public MonsterDef Def { get; internal set; }

    /// <summary>The <see cref="SpawnPoints"/> marker it belongs to; it respawns there.</summary>
    public string SpawnPointId { get; }

    /// <summary>Its AI state; home is where it spawned.</summary>
    public MonsterBrain Brain { get; }
}
