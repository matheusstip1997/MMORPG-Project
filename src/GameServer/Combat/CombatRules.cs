using Shared.Data;
using Shared.Data.Tables;
using Shared.Simulation.Stats;

namespace GameServer.Combat;

/// <summary>A character's saved HP, MP and stamina, in whole points.</summary>
public readonly record struct SavedMeters(int Hp, int Mp, int Stamina);

/// <summary>
/// What a world needs to build combatants and spawn monsters, from the loaded tables: the stat and damage config
/// (<see cref="CombatConfig"/>), <see cref="ClassDef"/>, <see cref="MonsterDef"/>, <see cref="SpawnPoints"/> and the
/// spec 12.7 values of <see cref="StartingValuesM1"/> (stamina, respawn delays, lag compensation cap). Immutable.
/// <para>
/// Time: the world's simulation time is <see cref="MsAt"/> (tick x tick length), so resource meters are deterministic
/// and need no wall clock (architecture rule 6). Meters are set at every spawn, so its origin never reaches a save.
/// </para>
/// </summary>
public sealed class CombatRules
{
    private readonly Dictionary<string, ClassDef> _classes;
    private readonly Dictionary<string, MonsterDef> _monsters;
    private readonly StartingValuesM1 _starting;

    private CombatRules(CombatConfig config, StartingValuesM1 starting, IReadOnlyList<ClassDef> classes,
        IReadOnlyList<MonsterDef> monsters, IReadOnlyList<SpawnPoints> spawnPoints, StrikeShapes shapes)
    {
        Config = config;
        _starting = starting;
        TickRateHz = starting.TickRateHz;
        _classes = classes.ToDictionary(c => c.Id, StringComparer.Ordinal);
        _monsters = monsters.ToDictionary(m => m.Id, StringComparer.Ordinal);
        SpawnPoints = spawnPoints.OrderBy(p => p.MarkerId, StringComparer.Ordinal).ToList();
        // The newest entry is this tick; the oldest is at least the cap back (R-3.1-03).
        LagCompensationCapTicks = TicksFor(starting.LagCompensationCapMs);
        HistoryCapacity = LagCompensationCapTicks + 1;
        ClockSync = new ClockSyncLimits(TickRateHz, LagCompensationCapTicks, starting.InterpolationDelayMs,
            starting.ClockSyncToleranceMs);
        UncoloredParryReductionPercent = starting.UncoloredDamageReductionPercent;
        MonsterRespawnTicks = TicksFor(starting.MonsterRespawnMs);
        BossRespawnTicks = TicksFor(starting.BossRespawnMs);
        Shapes = shapes;
        StaggerMs = starting.StaggerMs;
        StaggerImmunityTicks = TicksFor(starting.StaggerImmunityMs);
        PoiseRegenDelayTicks = TicksFor(starting.PoiseRegenDelayMs);
        PoiseRegenPercentPerSec = starting.PoiseRegenPercentPerSec;
        int largest = 1;
        foreach (var c in classes)
            largest = Math.Max(largest, c.BodyRadiusCm);
        foreach (var m in monsters)
            largest = Math.Max(largest, m.BodyRadiusCm);
        MaxBodyRadiusCm = largest;
        // How far anything can move during the rewind window (R-3.1-03), so a rewound target is still in the queried cells.
        RewindMarginCm = (int)((long)starting.MoveSpeedCmPerSec * starting.LagCompensationCapMs / 1000) + 1;
    }

    public CombatConfig Config { get; }

    public int TickRateHz { get; }

    /// <summary>R-3.1-03: the furthest a player's hit is rewound, <c>lagCompensationCapMs</c> in ticks (rounded up).</summary>
    public int LagCompensationCapTicks { get; }

    /// <summary>R-3.1-03: the view lag a client may claim (cap, round trip, interpolation, tolerance).</summary>
    internal ClockSyncLimits ClockSync { get; }

    /// <summary>R-3.3-04: what a parry takes off an uncolored hit's damage and poise damage (spec 12.7: 50%).</summary>
    public int UncoloredParryReductionPercent { get; }

    /// <summary>Entries of every combatant's <see cref="PositionHistory"/>: the lag compensation cap in ticks, plus now.</summary>
    public int HistoryCapacity { get; }

    /// <summary>The hit shapes of every Strike and GroundArea effect (spec 3.1, <c>HitShapeDef</c>).</summary>
    public StrikeShapes Shapes { get; }

    /// <summary>R-3.3-01: length of a poise break (<c>StartingValuesM1.staggerMs</c>).</summary>
    public int StaggerMs { get; }

    /// <summary>R-3.3-01: no poise damage and no new break this many ticks after a break (<c>staggerImmunityMs</c>).</summary>
    public int StaggerImmunityTicks { get; }

    /// <summary>R-3.3-01: poise starts to come back this many ticks after the last poise hit (<c>poiseRegenDelayMs</c>).</summary>
    public int PoiseRegenDelayTicks { get; }

    /// <summary>R-3.3-01: then this percent of the bar per second (<c>poiseRegenPercentPerSec</c>).</summary>
    public int PoiseRegenPercentPerSec { get; }

    /// <summary>The largest <c>bodyRadiusCm</c> of any class or monster (grid queries widen by it).</summary>
    public int MaxBodyRadiusCm { get; }

    /// <summary>Movement speed times the lag compensation cap: how far a target may have been at the rewound tick.</summary>
    public int RewindMarginCm { get; }

    /// <summary>Spec 12.7 monster respawn (<c>monsterRespawnMs</c>, 30 s) in ticks.</summary>
    public int MonsterRespawnTicks { get; }

    /// <summary>Spec 12.7 boss respawn (<c>bossRespawnMs</c>, 10 min) in ticks.</summary>
    public int BossRespawnTicks { get; }

    /// <summary>Every spawn point, in marker id order (the spawn order inside a tick).</summary>
    public IReadOnlyList<SpawnPoints> SpawnPoints { get; }

    /// <summary>The world's simulation time at the start of <paramref name="tick"/>, in ms.</summary>
    public long MsAt(uint tick) => (long)tick * 1000 / TickRateHz;

    /// <summary>Whole ticks covering <paramref name="ms"/>, rounded up.</summary>
    public int TicksFor(int ms) => (int)(((long)ms * TickRateHz + 999) / 1000);

    public bool TryGetClass(string id, out ClassDef classDef) => _classes.TryGetValue(id, out classDef!);

    public bool TryGetMonster(string id, out MonsterDef monster) => _monsters.TryGetValue(id, out monster!);

    public int RespawnTicks(MonsterDef monster) => monster.Boss ? BossRespawnTicks : MonsterRespawnTicks;

    /// <summary>
    /// A player of <paramref name="classDef"/> with full meters. Stamina is the same for every class
    /// (<c>StartingValuesM1.staminaPool</c>, <c>staminaRegenPerSec</c>, spec 12.7).
    /// TODO(open): the character's rank (spec 3.6) is not stored yet, so players are rank F.
    /// </summary>
    public Combatant ForPlayer(ClassDef classDef, long nowMs)
    {
        var stats = Stats(classDef);
        stats.SetBase(Config.Ids.MaxStamina, _starting.StaminaPool);
        stats.SetBase(Config.Ids.StaminaRegen, _starting.StaminaRegenPerSec);
        return Build(Faction.Player, classDef, Rank.F, classDef.ArmorClass, stats, nowMs);
    }

    /// <summary>
    /// Sets a character's saved HP, MP and stamina (save classes, spec 7.1); a value above the max is clamped when read.
    /// TODO(WP4): a character saved at 0 HP (dead) once player death exists.
    /// </summary>
    public static void Restore(Combatant combatant, SavedMeters saved, long nowMs)
    {
        combatant.Meters.Set(ResourceKind.Hp, saved.Hp, nowMs);
        combatant.Meters.Set(ResourceKind.Mp, saved.Mp, nowMs);
        combatant.Meters.Set(ResourceKind.Stamina, saved.Stamina, nowMs);
    }

    /// <summary>The meters a save writes (characters.hp, mp, stamina).</summary>
    public static SavedMeters Capture(Combatant combatant, long nowMs) => new(
        combatant.Hp(nowMs),
        combatant.Meters.Current(ResourceKind.Mp, nowMs),
        combatant.Meters.Current(ResourceKind.Stamina, nowMs));

    /// <summary>A monster of <paramref name="monster"/> with full meters.</summary>
    public Combatant ForMonster(MonsterDef monster, long nowMs) =>
        Build(Faction.Monster, monster, monster.Rank, ArmorClass.Light, Stats(monster), nowMs);

    /// <summary>
    /// Builds the rules from tables that passed the row validator. Adds the <see cref="CombatConfig"/> cross-table errors
    /// and returns null when any exists.
    /// </summary>
    public static CombatRules? TryBuild(ConfigSet set, List<ValidationError> errors)
    {
        var config = CombatConfig.TryBuild(set, errors);
        if (config == null)
            return null;
        return new CombatRules(config, set.Single<StartingValuesM1>(), Rows<ClassDef>(set), Rows<MonsterDef>(set),
            Rows<SpawnPoints>(set), StrikeShapes.From(Rows<HitShapeDef>(set)));
    }

    private Combatant Build(Faction faction, ICombatantTemplate template, Rank rank, ArmorClass armorClass, StatBlock stats, long nowMs) =>
        new(faction, template.Id, rank, armorClass, stats, new ResourceMeters(stats, Config, nowMs), template.PoiseMax,
            template.FlinchResist, template.FlinchDurationPercent, template.FlinchImmunityMs, template.BodyRadiusCm, HistoryCapacity);

    /// <summary>Base values of the stats code reads by name; every other stat starts at 0 (clamped by StatDef).</summary>
    private StatBlock Stats(ICombatantTemplate template)
    {
        var ids = Config.Ids;
        var stats = new StatBlock(Config.Stats);
        stats.SetBase(ids.MaxHp, template.MaxHp);
        stats.SetBase(ids.MaxMp, template.MaxMp);
        stats.SetBase(ids.HpRegen, template.HpRegen);
        stats.SetBase(ids.MpRegen, template.MpRegen);
        stats.SetBase(ids.Atk, template.Atk);
        stats.SetBase(ids.Matk, template.Matk);
        stats.SetBase(ids.Def, template.Def);
        stats.SetBase(ids.Mdef, template.Mdef);
        return stats;
    }

    private static List<T> Rows<T>(ConfigSet set)
    {
        var rows = new List<T>();
        if (set.TryGet(typeof(T), out var table))
            rows.AddRange(table.Rows.Cast<T>());
        return rows;
    }
}
