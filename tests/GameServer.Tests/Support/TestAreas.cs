using GameServer.Combat;
using GameServer.Entities;
using GameServer.Events;
using GameServer.World;
using Shared.Data;
using Shared.Data.Tables;

namespace GameServer.Tests.Support;

/// <summary>Records the build-up and ground area events a bus delivers (spec 3.8, 3.1, 8.1).</summary>
internal sealed class AreaEventLog : IEventConsumer<BuildupTriggered>, IEventConsumer<GroundAreaSpawned>, IEventConsumer<EffectApplied>,
    IEventConsumer<EffectRemoved>, IEventConsumer<EffectCleansed>, IEventConsumer<Healed>
{
    public List<IGameEvent> Events { get; } = [];

    public static AreaEventLog Attach(EventBus bus)
    {
        var log = new AreaEventLog();
        bus.Subscribe<BuildupTriggered>(log);
        bus.Subscribe<GroundAreaSpawned>(log);
        bus.Subscribe<EffectApplied>(log);
        bus.Subscribe<EffectRemoved>(log);
        bus.Subscribe<EffectCleansed>(log);
        bus.Subscribe<Healed>(log);
        return log;
    }

    public void Handle(in EventEnvelope envelope, in BuildupTriggered payload) => Events.Add(payload);

    public void Handle(in EventEnvelope envelope, in GroundAreaSpawned payload) => Events.Add(payload);

    public void Handle(in EventEnvelope envelope, in EffectApplied payload) => Events.Add(payload);

    public void Handle(in EventEnvelope envelope, in EffectRemoved payload) => Events.Add(payload);

    public void Handle(in EventEnvelope envelope, in EffectCleansed payload) => Events.Add(payload);

    public void Handle(in EventEnvelope envelope, in Healed payload) => Events.Add(payload);
}

/// <summary>
/// The repository's tables plus test effects, with the hit pipeline, statuses, build-ups and ground areas wired as
/// <see cref="GameWorld"/> wires them, and entities built by hand (no world): phases 5 and 6 run tick by tick.
/// </summary>
internal sealed class AreaFixture
{
    public const int NoCrit = 99;

    /// <summary>Hits enemies: 1000 ms with a tick every 300 ms (20 and 6 ticks: ticks at 0, 6, 12, 18), 50% physical, poise 5.</summary>
    public const string EnemyArea = "test.area.enemies";

    /// <summary>Heals the caster and allies 50% of Matk every 500 ms for 1000 ms.</summary>
    public const string HealArea = "test.area.heal";

    /// <summary>Damages enemies and heals allies.</summary>
    public const string MixedArea = "test.area.mixed";

    /// <summary>A RemoveStatus that only lowers build-ups (Simple Cleanse, R-3.8-04): self, 40 points.</summary>
    public const string Cleanse = "test.cleanse.buildUps";

    /// <summary>A RemoveStatus that cleanses debuffs from the caster.</summary>
    public const string CleanseDebuffs = "test.cleanse.debuffs";

    /// <summary>A fire circle like the old placeholder: 5000 ms every 500 ms (10 ticks), 40% fire magic and Burn +20 per tick, a circle of 3 m 3 m ahead.</summary>
    public const string FireArea = "test.fireCircle.area";

    /// <summary>The test action of every effect but <see cref="FireArea"/> (an instant skill), and its net id.</summary>
    public const string TestAction = "test.areas";
    public const int TestActionNetId = 4000;

    /// <summary>The action of <see cref="FireArea"/>: 200 ms startup, a 1500 ms channel, then the area (bindable to a hotbar slot).</summary>
    public const string FireCircle = "test.fireCircle";
    public const int FireCircleNetId = 4001;

    public AreaFixture(GroundAreaSettings? settings = null, Action<ConfigSet>? tune = null)
    {
        var tables = Tables();
        tune?.Invoke(tables);
        F = new HitFixture(new FixedRng(NoCrit), tables);
        Statuses = new StatusSystem(Grid, F.Rules, F.Resolver, F.Bus);
        BuildUps = new BuildUpSystem(F.Rules, Statuses, F.Bus);
        F.Resolver.BuildUps = BuildUps;
        Statuses.BuildUps = BuildUps;
        Settings = settings ?? new GroundAreaSettings { MaxPerOwner = 4, MaxActive = 16, EventRepeatTicks = 3 };
        Areas = new GroundAreaSystem(F.Rules, Settings, Grid, F.Resolver, Statuses, F.Bus);
        Log = AreaEventLog.Attach(F.Bus);
    }

    public HitFixture F { get; }

    public SpatialGrid Grid { get; } = new(1_500);

    public StatusSystem Statuses { get; }

    public BuildUpSystem BuildUps { get; }

    public GroundAreaSettings Settings { get; }

    public GroundAreaSystem Areas { get; }

    public AreaEventLog Log { get; }

    public List<Entity> Entities { get; } = [];

    public CombatRules Rules => F.Rules;

    public Player Player(uint id, int xCm = 0, int yCm = 0, string classId = "mage")
    {
        var player = F.Player(id, xCm, yCm, classId: classId);
        Track(player);
        return player;
    }

    public Monster Monster(uint id, int xCm = 0, int yCm = 0, int hp = 0)
    {
        var monster = F.Monster(id, xCm, yCm);
        if (hp > 0)
        {
            monster.Combatant!.Stats.SetBase(Rules.Config.Ids.MaxHp, hp);
            monster.Combatant.Meters.Set(Shared.Simulation.Stats.ResourceKind.Hp, hp, 0);
        }
        Track(monster);
        return monster;
    }

    public ActionEffectDef Effect(string id) => F.Effect(id);

    /// <summary>Places an area of <paramref name="effectId"/> where <paramref name="owner"/> stands and faces (phase 3).</summary>
    public GroundArea? Place(Entity owner, string effectId, uint tick) =>
        Areas.Spawn(owner, Effect(effectId), actionNetId: effectId == FireArea ? FireCircleNetId : TestActionNetId, effectIndex: 0, owner.Movement.Facing, tick);

    /// <summary>Phases 5 (areas, then due pending hits) and 6 (statuses) of one tick.</summary>
    public void Tick(uint tick)
    {
        Areas.Step(tick);
        F.Resolver.ResolveDue(tick);
        Statuses.Run(tick, Entities);
    }

    public void Run(uint from, uint to)
    {
        for (uint tick = from; tick <= to; tick++)
            Tick(tick);
    }

    /// <summary>The burn meter of <paramref name="target"/> in whole points.</summary>
    public int Burn(Entity target, uint tick) => BuildUps.PointsOf(target, "burn", tick);

    public List<IGameEvent> Dispatch()
    {
        F.Bus.DispatchQueued();
        return Log.Events;
    }

    private void Track(Entity entity)
    {
        Grid.Add(entity);
        Entities.Add(entity);
        Entities.Sort((a, b) => a.Id.CompareTo(b.Id));
    }

    /// <summary>The repository's tables with the test rows of this fixture.</summary>
    private static ConfigSet Tables()
    {
        var set = TestCombat.Tables();
        AddTestRows(set);
        return set;
    }

    /// <summary>
    /// Adds this fixture's actions, effects, shapes (circles of 3 m on the caster; the fire circle's 3 m ahead) and the fire
    /// circle's Burn +20, so the tests do not depend on the kits' rows.
    /// </summary>
    public static void AddTestRows(ConfigSet set)
    {
        var actions = new List<ActionDef>(Rows<ActionDef>(set))
        {
            new()
            {
                Id = TestAction, NetId = TestActionNetId, Kind = ActionKind.Skill, StartupMs = 100, ActiveMs = 100, RecoveryMs = 100,
                CancelFrom = ActionPhase.Startup, StaminaCost = 10, MpCost = 1,
            },
            new()
            {
                Id = FireCircle, NetId = FireCircleNetId, Kind = ActionKind.Skill, StartupMs = 200, ChannelMs = 1500, ActiveMs = 100,
                RecoveryMs = 300, CooldownMs = 12_000, BufferWindowMs = 150, CancelFrom = ActionPhase.Startup, StaminaCost = 30, MpCost = 15,
            },
        };
        var effects = new List<ActionEffectDef>(Rows<ActionEffectDef>(set))
        {
            Area(EnemyArea, [EffectTarget.Enemies], durationMs: 1000, intervalMs: 300, damage: 50),
            Area(HealArea, [EffectTarget.Self, EffectTarget.Allies], durationMs: 1000, intervalMs: 500, heal: 50),
            Area(MixedArea, [EffectTarget.Allies, EffectTarget.Enemies], durationMs: 1000, intervalMs: 500, damage: 50, heal: 50),
            new()
            {
                Id = FireArea, ActionId = FireCircle, Kind = EffectKind.GroundArea, Phase = ActionPhase.Active, Targets = [EffectTarget.Enemies],
                DurationMs = 5000, TickIntervalMs = 500, PoiseDamage = 5, DamagePercent = 40, DamageKind = EffectDamageKind.Magic,
                Element = Element.Fire,
            },
            new()
            {
                Id = Cleanse, ActionId = TestAction, Kind = EffectKind.RemoveStatus, Phase = ActionPhase.Active,
                Targets = [EffectTarget.Self], BuildUpReduction = 40,
            },
            new()
            {
                Id = CleanseDebuffs, ActionId = TestAction, Kind = EffectKind.RemoveStatus, Phase = ActionPhase.Active,
                Targets = [EffectTarget.Self], StatusKind = StatusEffectKind.Debuff,
            },
        };
        var shapes = new List<HitShapeDef>(Rows<HitShapeDef>(set));
        foreach (var id in new[] { EnemyArea, HealArea, MixedArea })
            shapes.Add(new HitShapeDef { Id = id + ".circle", EffectId = id, Kind = HitShapeKind.Capsule, RadiusCm = 300 });
        shapes.Add(new HitShapeDef { Id = FireArea + ".circle", EffectId = FireArea, Kind = HitShapeKind.Capsule, ForwardOffsetCm = 300, RadiusCm = 300 });
        set.Add(new ConfigTable(typeof(ActionDef), 2, actions.ToArray()));
        set.Add(new ConfigTable(typeof(ActionEffectDef), 2, effects.ToArray()));
        set.Add(new ConfigTable(typeof(HitShapeDef), 1, shapes.ToArray()));
        AddChanges(set, (FireArea, 20));
    }

    /// <summary>Adds build-up changes (<c>EffectBuildUpDef</c> rows) to the tables.</summary>
    public static void AddChanges(ConfigSet set, params (string EffectId, int Amount)[] changes)
    {
        var rows = new List<EffectBuildUpDef>(Rows<EffectBuildUpDef>(set));
        foreach (var (effectId, amount) in changes)
            rows.Add(new EffectBuildUpDef { Id = effectId + ".burn.test", EffectId = effectId, BuildUpId = "burn", Amount = amount });
        set.Add(new ConfigTable(typeof(EffectBuildUpDef), 1, rows.ToArray()));
    }

    private static ActionEffectDef Area(string id, EffectTarget[] targets, int durationMs, int intervalMs, int damage = 0, int heal = 0) => new()
    {
        Id = id, ActionId = TestAction, Kind = EffectKind.GroundArea, Phase = ActionPhase.Active, Targets = targets,
        DurationMs = durationMs, TickIntervalMs = intervalMs, PoiseDamage = damage > 0 ? 5 : 0, DamagePercent = damage,
        DamageKind = damage > 0 ? EffectDamageKind.Physical : EffectDamageKind.None, HealPercent = heal,
    };

    private static IEnumerable<T> Rows<T>(ConfigSet set) =>
        set.TryGet(typeof(T), out var table) ? table.Rows.Cast<T>() : [];
}
