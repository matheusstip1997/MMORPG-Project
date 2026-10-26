using GameServer.Combat;
using GameServer.Entities;
using GameServer.World.Map;
using Shared.Data.Tables;

namespace GameServer.World;

/// <summary>
/// Fixed monster spawn points (backlog F, spec 4.2 <c>SpawnController</c>, spec 12.7 respawn delays): one monster per
/// <see cref="SpawnPoints"/> row at its monsterSpawn marker. Phase 7 spawns every point whose monster is missing and whose
/// respawn time has come (all of them on the first tick); phase 8 removes dead monsters and starts their point's respawn
/// delay (<c>monsterRespawnMs</c>, or <c>bossRespawnMs</c> for a boss). Points are handled in marker id order.
/// TODO(open): how long a dead monster stays in the world before it is removed (death animation, loot); for now it goes
/// in the phase 8 of the tick it died. TODO(F, spec 4.2): population caps, spawning only near players, the demand
/// multiplier on the delay. Simulation thread only; allocates only when a monster spawns.
/// </summary>
internal sealed class SpawnController
{
    private readonly Point[] _points;

    /// <exception cref="ArgumentException">A spawn point's marker or monster is missing (the loaders report these first).</exception>
    public SpawnController(CombatRules rules, WorldMap map)
    {
        var errors = WorldLoader.CheckSpawnPoints(map, rules.SpawnPoints);
        if (errors.Count > 0)
            throw new ArgumentException(errors[0].Message, nameof(map));
        _points = new Point[rules.SpawnPoints.Count];
        for (int i = 0; i < _points.Length; i++)
        {
            var row = rules.SpawnPoints[i];
            if (!rules.TryGetMonster(row.MonsterId, out var monster))
                throw new ArgumentException($"SpawnPoints '{row.MarkerId}' names unknown monster '{row.MonsterId}'", nameof(rules));
            map.TryGetMarker(row.MarkerId, out var marker);
            _points[i] = new Point(row, monster, marker, rules.RespawnTicks(monster));
        }
    }

    public int PointCount => _points.Length;

    /// <summary>
    /// Dev hot reload (spec 11.7): monsters spawned from now on are built from the reloaded <see cref="MonsterDef"/> rows
    /// (same ids: the reload keeps every monster and every spawn point), with the reloaded respawn delays.
    /// </summary>
    public void Retune(CombatRules rules)
    {
        foreach (var point in _points)
        {
            if (!rules.TryGetMonster(point.Monster.Id, out var monster))
                continue; // not reached: the reload refuses a removed monster
            point.Monster = monster;
            point.RespawnTicks = rules.RespawnTicks(monster);
        }
    }

    /// <summary>Phase 7: spawns each missing monster whose respawn tick has come.</summary>
    public void SpawnDue(uint tick, Func<MonsterDef, WorldMarker, string, Monster> spawn)
    {
        foreach (var point in _points)
        {
            if (point.Live == null && (int)(tick - point.RespawnAtTick) >= 0)
                point.Live = spawn(point.Monster, point.Marker, point.Row.MarkerId);
        }
    }

    /// <summary>Phase 8: removes each dead monster and schedules its point's respawn.</summary>
    public void RemoveDead(uint tick, Action<Monster> remove)
    {
        foreach (var point in _points)
        {
            if (point.Live?.Combatant is { IsAlive: false })
            {
                remove(point.Live);
                point.Live = null;
                point.RespawnAtTick = tick + (uint)point.RespawnTicks;
            }
        }
    }

    private sealed class Point
    {
        public Point(SpawnPoints row, MonsterDef monster, WorldMarker marker, int respawnTicks)
        {
            Row = row;
            Monster = monster;
            Marker = marker;
            RespawnTicks = respawnTicks;
        }

        public SpawnPoints Row { get; }
        public MonsterDef Monster { get; set; }
        public WorldMarker Marker { get; }
        public int RespawnTicks { get; set; }
        public Monster? Live { get; set; }
        public uint RespawnAtTick { get; set; }
    }
}
