using GameServer.Combat;
using GameServer.Entities;
using GameServer.Events;
using GameServer.Net;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Simulation;
using Shared.Simulation.Actions;
using Shared.Util;

namespace GameServer.Tests.Support;

/// <summary>An <see cref="IRng"/> that always draws the same value (crit roll 0 always crits, 99 never does).</summary>
public sealed class FixedRng(int value) : IRng
{
    public int Draws { get; private set; }

    public uint NextUInt() => (uint)Draw();

    public int NextInt(int maxExclusive) => Draw() % maxExclusive;

    public int NextInt(int minInclusive, int maxExclusive) => minInclusive + Draw() % (maxExclusive - minInclusive);

    public bool RollPercent(int percent) => Draw() < percent;

    private int Draw()
    {
        Draws++;
        return value;
    }
}

/// <summary>Records the combat events a bus delivers, in delivery order.</summary>
public sealed class CombatEventLog :
    IEventConsumer<HitLanded>, IEventConsumer<Damaged>, IEventConsumer<Staggered>, IEventConsumer<Flinched>,
    IEventConsumer<Killed>, IEventConsumer<Died>
{
    public List<IGameEvent> Events { get; } = [];

    public List<long> Actors { get; } = [];

    public static CombatEventLog Attach(EventBus bus)
    {
        var log = new CombatEventLog();
        bus.Subscribe<HitLanded>(log);
        bus.Subscribe<Damaged>(log);
        bus.Subscribe<Staggered>(log);
        bus.Subscribe<Flinched>(log);
        bus.Subscribe<Killed>(log);
        bus.Subscribe<Died>(log);
        return log;
    }

    public void Handle(in EventEnvelope envelope, in HitLanded payload) => Add(envelope, payload);

    public void Handle(in EventEnvelope envelope, in Damaged payload) => Add(envelope, payload);

    public void Handle(in EventEnvelope envelope, in Staggered payload) => Add(envelope, payload);

    public void Handle(in EventEnvelope envelope, in Flinched payload) => Add(envelope, payload);

    public void Handle(in EventEnvelope envelope, in Killed payload) => Add(envelope, payload);

    public void Handle(in EventEnvelope envelope, in Died payload) => Add(envelope, payload);

    public IEnumerable<Type> Kinds => Events.Select(e => e.GetType());

    private void Add(in EventEnvelope envelope, IGameEvent payload)
    {
        Events.Add(payload);
        Actors.Add(envelope.ActorCharacterId);
    }
}

/// <summary>
/// The repository's tables, actions and combat rules with a <see cref="HitResolver"/> on its own bus, and entities built
/// by hand (no world), for testing the per-target pipeline of spec 3.1.
/// </summary>
internal sealed class HitFixture
{
    public HitFixture(IRng? rng = null, ConfigSet? tables = null)
    {
        Tables = tables ?? TestCombat.Tables();
        Rules = TestCombat.Rules(Tables);
        Catalog = ActionCatalog.From(Tables);
        Bus = new EventBus(1, Clock, TestPipelineSettings.BusLimits());
        Log = CombatEventLog.Attach(Bus);
        Rng = rng ?? new Pcg32Rng(42);
        Resolver = new HitResolver(Rules, Catalog, Bus, Clock, Rng);
    }

    public ConfigSet Tables { get; }

    public CombatRules Rules { get; }

    public ActionCatalog Catalog { get; }

    public FakeClock Clock { get; } = new();

    public EventBus Bus { get; }

    public CombatEventLog Log { get; }

    public IRng Rng { get; }

    public HitResolver Resolver { get; }

    public ClassDef Class(string id = "warrior")
    {
        Assert.True(Rules.TryGetClass(id, out var def));
        return def;
    }

    public MonsterDef MonsterDef(string id = TestCombat.MonsterId)
    {
        Assert.True(Rules.TryGetMonster(id, out var def));
        return def;
    }

    public ActionEffectDef Effect(string id)
    {
        Assert.True(Tables.TryGet(typeof(ActionEffectDef), out var table));
        return table.Rows.Cast<ActionEffectDef>().Single(e => e.Id == id);
    }

    public Player Player(uint id, int xCm = 0, int yCm = 0, ushort facing = 0, string classId = "warrior", long characterId = 0)
    {
        var position = FixVec2.FromCm(xCm, yCm);
        var player = new Player(id, Entities.Player.NoPeer, id, new InputQueue(4), new MovementState(position, facing), 0,
            characterId == 0 ? 1_000 + id : characterId)
        {
            Actions = new ActionMachineState(Catalog),
            Combatant = Rules.ForPlayer(Class(classId), 0),
        };
        player.Combatant!.History.Record(0, position);
        return player;
    }

    public Monster Monster(uint id, int xCm = 0, int yCm = 0, string monsterId = TestCombat.MonsterId)
    {
        var position = FixVec2.FromCm(xCm, yCm);
        var def = MonsterDef(monsterId);
        var monster = new Monster(id, def, TestCombat.MonsterSpawnId, new MovementState(position, 0), 0, Rules.ForMonster(def, 0))
        {
            Actions = new ActionMachineState(Catalog),
        };
        monster.Combatant!.History.Record(0, position);
        return monster;
    }

    public HitOutcome Hit(Entity attacker, Entity target, string effectId = "attack.basic.strike", uint tick = 10,
        HitSource source = HitSource.Strike, int damagePercent = 100) =>
        Resolver.Resolve(new HitRequest(attacker, target, Effect(effectId), source, tick, damagePercent));

    /// <summary>Delivers the queued events (phase 11) and returns everything the log holds.</summary>
    public List<IGameEvent> Dispatch()
    {
        Bus.DispatchQueued();
        return Log.Events;
    }

    public long NowMs(uint tick) => Rules.MsAt(tick);
}
