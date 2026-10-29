using GameServer.Combat;
using GameServer.World.Map;
using Shared.Data.Tables;
using Shared.Simulation;
using Shared.Simulation.Actions;

namespace GameServer.AI;

/// <summary>
/// What the monster AI of one world runs with (backlog F), built once from the tables: <see cref="MonsterAiSettings"/> in
/// ticks, each monster type's attacks (<see cref="MonsterAttack"/>, in <see cref="MonsterDef.Attacks"/> order), and which
/// region indexes of the map monsters never enter. Immutable.
/// </summary>
public sealed class MonsterAiRules
{
    private readonly Dictionary<string, MonsterAttack[]> _attacks = new(StringComparer.Ordinal);
    private readonly bool[] _noEntry = new bool[256];

    /// <param name="projectiles">Without projectile tables, projectile attacks never reach anything.</param>
    public MonsterAiRules(MonsterAiSettings settings, CombatRules combat, ActionCatalog actions, WorldMap map,
        ProjectileRules? projectiles)
    {
        Settings = settings;
        TickRateHz = combat.TickRateHz;
        ThinkTicks = Math.Max(1, combat.TicksFor(settings.ThinkIntervalMs));
        LeashTimeoutTicks = Math.Max(1, combat.TicksFor(settings.LeashTimeoutMs));
        HomeToleranceCm = settings.HomeToleranceCm;
        Bodies = combat.Bodies;
        MaxBodyRadiusRaw = combat.MaxBodyRadiusRaw;
        MoveSpeedPercentStat = combat.Config.Ids.MoveSpeedPercent;
        foreach (var monster in combat.Monsters)
        {
            var list = new List<MonsterAttack>();
            foreach (var id in monster.Attacks)
            {
                var action = actions.Find(id);
                if (action != null)
                    list.Add(MonsterAttack.From(action, combat.Shapes, projectiles));
            }
            _attacks.Add(monster.Id, list.ToArray());
        }
        foreach (var region in map.Regions)
        {
            if (Array.IndexOf(settings.NoEntryAreaTypes, region.AreaType) >= 0)
                _noEntry[region.Index] = true;
        }
    }

    public MonsterAiSettings Settings { get; }

    public int TickRateHz { get; }

    /// <summary>An idle monster scans for players every this many ticks (<c>thinkIntervalMs</c>).</summary>
    public int ThinkTicks { get; }

    /// <summary>A monster going home that gets no closer for this many ticks is placed at home (<c>leashTimeoutMs</c>).</summary>
    public int LeashTimeoutTicks { get; }

    public int HomeToleranceCm { get; }

    /// <summary>R-2.2-07: how bodies block a monster's walk (<c>BodyCollisionSettings</c>; off without the table).</summary>
    public BodyCollisionRules Bodies { get; }

    /// <summary>The largest body radius of any class or monster, raw (how far a body gather reaches).</summary>
    public int MaxBodyRadiusRaw { get; }

    /// <summary>The <c>moveSpeedPercent</c> stat (WP-M2-9: Cold's slow), or -1 when StatDef has no row.</summary>
    public int MoveSpeedPercentStat { get; }

    /// <summary>The monster type's attacks in priority order; empty for an unknown type.</summary>
    public MonsterAttack[] AttacksOf(MonsterDef monster) =>
        _attacks.TryGetValue(monster.Id, out var attacks) ? attacks : Array.Empty<MonsterAttack>();

    /// <summary>Monsters never enter this region (user decision 2026-10-22: <c>MonsterAiSettings.noEntryAreaTypes</c>).</summary>
    public bool IsNoEntry(byte regionIndex) => _noEntry[regionIndex];

    public bool IsNoEntry(WorldMap map, FixVec2 position) => _noEntry[map.Grid.RegionIndexAt(position)];
}
