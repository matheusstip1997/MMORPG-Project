using GameServer.AI;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Simulation;
using Shared.Simulation.Actions;
using Shared.Simulation.Combat;
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
    private readonly Dictionary<int, MonsterDef> _monstersByNetId;
    private readonly StartingValuesM1 _starting;

    private CombatRules(CombatConfig config, StartingValuesM1 starting, IReadOnlyList<ClassDef> classes,
        IReadOnlyList<MonsterDef> monsters, IReadOnlyList<SpawnPoints> spawnPoints, StrikeShapes shapes,
        MonsterAiSettings? monsterAi, IReadOnlyList<StatusEffectDef> statuses, IReadOnlyList<StatusModifierDef> statusModifiers,
        IReadOnlyList<BuildUpDef> buildUps, IReadOnlyList<EffectBuildUpDef> buildUpChanges, GroundAreaSettings? groundAreas,
        BodyCollisionSettings? bodies, ConsumptionMeterSettings? consumption)
    {
        Config = config;
        _starting = starting;
        TickRateHz = starting.TickRateHz;
        _classes = classes.ToDictionary(c => c.Id, StringComparer.Ordinal);
        _monsters = monsters.ToDictionary(m => m.Id, StringComparer.Ordinal);
        _monstersByNetId = monsters.ToDictionary(m => m.NetId);
        Monsters = monsters.OrderBy(m => m.Id, StringComparer.Ordinal).ToList();
        Classes = classes.OrderBy(c => c.Id, StringComparer.Ordinal).ToList();
        MonsterAi = monsterAi;
        SpawnPoints = spawnPoints.OrderBy(p => p.MarkerId, StringComparer.Ordinal).ToList();
        // The newest entry is this tick; the oldest is at least the cap back (R-3.1-03).
        LagCompensationCapTicks = TicksFor(starting.LagCompensationCapMs);
        HistoryCapacity = LagCompensationCapTicks + 1;
        ClockSync = new ClockSyncLimits(TickRateHz, LagCompensationCapTicks, starting.InterpolationDelayMs,
            starting.ClockSyncToleranceMs);
        UncoloredParryReductionPercent = starting.UncoloredDamageReductionPercent;
        MonsterRespawnTicks = TicksFor(starting.MonsterRespawnMs);
        BossRespawnTicks = TicksFor(starting.BossRespawnMs);
        PlayerRespawnTicks = TicksFor(starting.PlayerRespawnDelayMs);
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
        MaxBodyRadiusRaw = FixedPoint.FromCm(largest);
        BodySettings = bodies;
        Bodies = BodyCollisionRules.From(bodies);
        // How far anything can move during the rewind window (R-3.1-03), so a rewound target is still in the queried cells.
        int fastest = starting.MoveSpeedCmPerSec;
        foreach (var m in monsters)
            fastest = Math.Max(fastest, m.MoveSpeedCmPerSec);
        RewindMarginCm = (int)((long)fastest * starting.LagCompensationCapMs / 1000) + 1;
        Statuses = StatusCatalog.From(statuses, statusModifiers, config, TicksFor);
        BuildUps = BuildUpCatalog.From(buildUps, buildUpChanges, Statuses, config, TicksFor, TickRateHz);
        GroundAreas = groundAreas;
        Consumption = consumption ?? GameServer.Stats.ConsumptionMeters.Empty;
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

    /// <summary>The hit shapes of every effect that has them (spec 3.1, <c>HitShapeDef</c>).</summary>
    public StrikeShapes Shapes { get; }

    /// <summary>Every status (spec 3.7, <c>StatusEffectDef</c>), with times in ticks.</summary>
    internal StatusCatalog Statuses { get; }

    /// <summary>Every build-up and the build-up changes of hits (spec 3.8, <c>BuildUpDef</c>, <c>EffectBuildUpDef</c>).</summary>
    internal BuildUpCatalog BuildUps { get; }

    /// <summary>Limits of the ground areas (spec 3.1, <c>GroundAreaSettings</c>); null when the table was not loaded: areas then do nothing.</summary>
    public GroundAreaSettings? GroundAreas { get; }

    /// <summary>
    /// R-3.4-03 toxicity and satiety caps and decay (<c>ConsumptionMeterSettings</c>, WP-M2-2); caps of 0 when the table was
    /// not loaded, so the meters stay empty.
    /// </summary>
    public ConsumptionMeterSettings Consumption { get; }

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

    /// <summary><see cref="MaxBodyRadiusCm"/> in raw Q24.8 cm (how far a body gather reaches past the mover).</summary>
    public int MaxBodyRadiusRaw { get; }

    /// <summary>R-2.2-07 body collision (<c>BodyCollisionSettings</c>); null when the table was not loaded: bodies then block nothing.</summary>
    public BodyCollisionSettings? BodySettings { get; }

    /// <summary>R-2.2-07 body collision in raw units; <see cref="BodyCollisionRules.Off"/> without the table.</summary>
    public BodyCollisionRules Bodies { get; }

    /// <summary>
    /// The fastest walk (players or monsters) times the lag compensation cap: how far a target may have been at the rewound
    /// tick. TODO(D3): dashes move faster than any walk; a target dashing during the rewind window can fall outside it.
    /// </summary>
    public int RewindMarginCm { get; }

    /// <summary>Spec 12.7 monster respawn (<c>monsterRespawnMs</c>, 30 s) in ticks.</summary>
    public int MonsterRespawnTicks { get; }

    /// <summary>Spec 12.7 boss respawn (<c>bossRespawnMs</c>, 10 min) in ticks.</summary>
    public int BossRespawnTicks { get; }

    /// <summary>A dead player respawns this many ticks after death (<c>playerRespawnDelayMs</c>, user decision 2026-10-22).</summary>
    public int PlayerRespawnTicks { get; }

    /// <summary>Every spawn point, in marker id order (the spawn order inside a tick).</summary>
    public IReadOnlyList<SpawnPoints> SpawnPoints { get; }

    /// <summary>The world's simulation time at the start of <paramref name="tick"/>, in ms.</summary>
    public long MsAt(uint tick) => (long)tick * 1000 / TickRateHz;

    /// <summary>Whole ticks covering <paramref name="ms"/>, rounded up.</summary>
    public int TicksFor(int ms) => (int)(((long)ms * TickRateHz + 999) / 1000);

    public bool TryGetClass(string id, out ClassDef classDef) => _classes.TryGetValue(id, out classDef!);

    /// <summary>Every class, in id order.</summary>
    public IReadOnlyList<ClassDef> Classes { get; }

    public bool TryGetMonster(string id, out MonsterDef monster) => _monsters.TryGetValue(id, out monster!);

    /// <summary>A monster type by <c>MonsterDef.netId</c>, what <c>Killed.VictimDefId</c> carries.</summary>
    public bool TryGetMonster(int netId, out MonsterDef monster) => _monstersByNetId.TryGetValue(netId, out monster!);

    /// <summary>Every monster type, in id order.</summary>
    public IReadOnlyList<MonsterDef> Monsters { get; }

    /// <summary>The monster AI settings (backlog F); null when the table was not loaded: monsters then stand still.</summary>
    public MonsterAiSettings? MonsterAi { get; }

    public int RespawnTicks(MonsterDef monster) => monster.Boss ? BossRespawnTicks : MonsterRespawnTicks;

    /// <summary>A level-1 player of <paramref name="classDef"/> with full meters (<see cref="ForPlayer(ClassDef, int, long, Rank)"/>).</summary>
    public Combatant ForPlayer(ClassDef classDef, long nowMs, Rank rank = Rank.F) => ForPlayer(classDef, 1, nowMs, rank);

    /// <summary>
    /// A player of <paramref name="classDef"/> at <paramref name="level"/> with full meters. Its stat block has the attribute
    /// formula (spec 3.4 "M2 primary attributes"): STR, DEX, STA and INT from the class and the level, and the class's
    /// derived stats as the base the formula adds to. Stamina is the same for every class
    /// (<c>StartingValuesM1.staminaPool</c>, <c>staminaRegenPerSec</c>, spec 12.7).
    /// <paramref name="rank"/> is the character's saved rank (spec 3.6, WP-M2-3).
    /// </summary>
    public Combatant ForPlayer(ClassDef classDef, int level, long nowMs, Rank rank = Rank.F)
    {
        var stats = Stats(classDef, Config.Attributes);
        stats.SetBase(Config.Ids.MaxStamina, _starting.StaminaPool);
        stats.SetBase(Config.Ids.StaminaRegen, _starting.StaminaRegenPerSec);
        Attributes.SetBases(stats, Config.Attributes, classDef, level);
        return Build(Faction.Player, classDef, rank, classDef.ArmorClass, stats, nowMs);
    }

    /// <summary>
    /// A player's level changed (spec 3.4, WP-M2-3's <c>LevelUp</c>): its attributes are set again for the level, and the
    /// meters settle at once so a new max or regeneration rate starts exactly then. Changes nothing for a monster.
    /// </summary>
    public void SetLevel(Combatant combatant, ClassDef classDef, int level, long nowMs)
    {
        if (combatant.Stats.Formula is not { } formula)
            return;
        combatant.Meters.Settle(nowMs);
        Attributes.SetBases(combatant.Stats, formula, classDef, level);
        combatant.Meters.Settle(nowMs);
    }

    /// <summary>
    /// Sets a character's saved HP, MP and stamina (save classes, spec 7.1); a value above the max is clamped when read.
    /// A character saved at 0 HP (it died) is not restored this way: it logs in at the city spawn with full meters
    /// (<c>GameWorld.SpawnPlayer</c>, user decision 2026-10-22).
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
    public Combatant ForMonster(MonsterDef monster, long nowMs)
    {
        var combatant = Build(Faction.Monster, monster, monster.Rank, ArmorClass.Light, Stats(monster), nowMs);
        combatant.StatusImmuneTags = monster.StatusImmuneTags; // R-3.7-02
        return combatant;
    }

    /// <summary>
    /// Builds the rules from tables that passed the row validator. Adds the <see cref="CombatConfig"/> cross-table errors
    /// and returns null when any exists.
    /// </summary>
    public static CombatRules? TryBuild(ConfigSet set, List<ValidationError> errors)
    {
        var config = CombatConfig.TryBuild(set, errors);
        if (config == null)
            return null;
        var rules = new CombatRules(config, set.Single<StartingValuesM1>(), Rows<ClassDef>(set), Rows<MonsterDef>(set),
            Rows<SpawnPoints>(set), StrikeShapes.From(Rows<HitShapeDef>(set)), Rows<MonsterAiSettings>(set).FirstOrDefault(),
            Rows<StatusEffectDef>(set), Rows<StatusModifierDef>(set), Rows<BuildUpDef>(set), Rows<EffectBuildUpDef>(set),
            Rows<GroundAreaSettings>(set).FirstOrDefault(), Rows<BodyCollisionSettings>(set).FirstOrDefault(),
            Rows<ConsumptionMeterSettings>(set).FirstOrDefault());
        int before = errors.Count;
        CheckBodies(set, rules, errors);
        return errors.Count > before ? null : rules;
    }

    /// <summary>
    /// The cross-table rules of body collision (R-2.2-07, spec 2.2 "Body collision (M1)"): the overlap tolerance is below
    /// the smallest body radius (or two small bodies could pass through each other); the largest pair of bodies fits the
    /// shared sweep (<see cref="SweptCircle.MaxRaw"/>); and every monster's shortest attack hits the smallest class body
    /// standing at contact in front of it, since the AI walks until that attack reaches (spec 3.1, monster AI) and a body
    /// stops it at contact: otherwise it would stand there and never attack.
    /// </summary>
    private static void CheckBodies(ConfigSet set, CombatRules rules, List<ValidationError> errors)
    {
        if (rules.BodySettings is not { } settings)
            return;
        string table = TableName<BodyCollisionSettings>();
        int smallestClass = rules.Classes.Count == 0 ? 0 : rules.Classes.Min(c => c.BodyRadiusCm);
        int smallest = rules.Classes.Select(c => c.BodyRadiusCm).Concat(rules.Monsters.Select(m => m.BodyRadiusCm))
            .DefaultIfEmpty(int.MaxValue).Min();
        if (settings.OverlapToleranceCm >= smallest)
            errors.Add(new ValidationError(ValidationCodes.RuleBroken, table, 0, nameof(BodyCollisionSettings.OverlapToleranceCm),
                $"must be below the smallest bodyRadiusCm ({smallest})"));
        if ((long)rules.MaxBodyRadiusRaw * 2 > SweptCircle.MaxRaw)
            errors.Add(new ValidationError(ValidationCodes.RuleBroken, table, 0, null,
                $"two of the largest bodies ({rules.MaxBodyRadiusCm} cm) exceed the body sweep's {SweptCircle.MaxRaw >> FixedPoint.FractionBits} cm"));

        if (rules.MonsterAi == null || smallestClass == 0 || !set.TryGet(typeof(ActionDef), out _))
            return;
        var actions = ActionCatalog.From(set);
        var projectiles = set.TryGet(typeof(ProjectileSettings), out _) ? ProjectileRules.From(set) : null;
        int classRadiusRaw = FixedPoint.FromCm(smallestClass);
        foreach (var monster in rules.Monsters)
        {
            MonsterAttack? shortest = null;
            foreach (var id in monster.Attacks)
            {
                var action = actions.Find(id);
                if (action == null)
                    continue;
                var attack = MonsterAttack.From(action, rules.Shapes, projectiles);
                if (shortest == null || attack.NominalReachCm < shortest.NominalReachCm)
                    shortest = attack;
            }
            if (shortest == null)
                continue;
            int contact = FixedPoint.FromCm(monster.BodyRadiusCm) + classRadiusRaw - rules.Bodies.OverlapToleranceRaw;
            if (!shortest.WouldHit(FixVec2.Zero, 0, new FixVec2(0, contact), classRadiusRaw))
                errors.Add(new ValidationError(ValidationCodes.RuleBroken, TableName<MonsterDef>(), null, nameof(MonsterDef.Attacks),
                    $"{monster.Id}: its shortest attack does not reach a {smallestClass} cm body at contact (R-2.2-07)"));
        }
    }

    private static string TableName<T>() =>
        ((TableAttribute)Attribute.GetCustomAttribute(typeof(T), typeof(TableAttribute))!).Name;

    private Combatant Build(Faction faction, ICombatantTemplate template, Rank rank, ArmorClass armorClass, StatBlock stats, long nowMs) =>
        new(faction, template.Id, rank, armorClass, stats, new ResourceMeters(stats, Config, nowMs), template.PoiseMax,
            Config.Ids.FlinchResist, template.FlinchDurationPercent, template.FlinchImmunityMs, template.BodyRadiusCm, HistoryCapacity)
        {
            BuildUps = new BuildUpMeter[BuildUps.Count], // spec 3.8: one empty meter per build-up
        };

    /// <summary>
    /// Base values of the stats code reads by name; every other stat starts at 0 (clamped by StatDef). Players get the
    /// attribute formula, monsters none (their <c>MonsterDef</c> stats stay flat).
    /// </summary>
    private StatBlock Stats(ICombatantTemplate template, AttributeFormulaRules? formula = null)
    {
        var ids = Config.Ids;
        var stats = new StatBlock(Config.Stats, formula);
        stats.SetBase(ids.MaxHp, template.MaxHp);
        stats.SetBase(ids.MaxMp, template.MaxMp);
        stats.SetBase(ids.HpRegen, template.HpRegen);
        stats.SetBase(ids.MpRegen, template.MpRegen);
        stats.SetBase(ids.Atk, template.Atk);
        stats.SetBase(ids.Matk, template.Matk);
        stats.SetBase(ids.Def, template.Def);
        stats.SetBase(ids.Mdef, template.Mdef);
        stats.SetBase(ids.FlinchResist, template.FlinchResist);
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
