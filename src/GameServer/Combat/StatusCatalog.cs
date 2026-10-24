using Shared.Data.Tables;
using Shared.Simulation.Stats;

namespace GameServer.Combat;

/// <summary>One stat change of a status, resolved to its stat index (spec 3.7, <see cref="StatusModifierDef"/>).</summary>
internal readonly record struct StatusModifier(int StatId, int Flat, int Percent);

/// <summary>A <see cref="StatusEffectDef"/> with its modifiers and times in ticks, built once (spec 3.7, backlog D6).</summary>
internal sealed class StatusDefinition
{
    public StatusDefinition(StatusEffectDef def, StatusModifier[] modifiers, int durationTicks, int tickIntervalTicks, int tickElement)
    {
        Def = def;
        Modifiers = modifiers;
        DurationTicks = durationTicks;
        TickIntervalTicks = tickIntervalTicks;
        TickElement = tickElement;
    }

    public StatusEffectDef Def { get; }

    public string Id => Def.Id;

    public int NetId => Def.NetId;

    public StatusEffectKind Kind => Def.Kind;

    public StatusModifier[] Modifiers { get; }

    /// <summary><c>durationMs</c> rounded up to whole ticks (at least one).</summary>
    public int DurationTicks { get; }

    /// <summary><c>tickIntervalMs</c> rounded up to whole ticks; 0 without periodic ticks.</summary>
    public int TickIntervalTicks { get; }

    /// <summary>The tick damage's ElementCaps index (<see cref="DamageCalc.NoElement"/> for none).</summary>
    public int TickElement { get; }

    /// <summary>Stat changes for buffs are source type buff, for debuffs debuff (spec 3.7).</summary>
    public StatSourceType SourceType => Kind == StatusEffectKind.Debuff ? StatSourceType.Debuff : StatSourceType.Buff;
}

/// <summary>Every status of the config, by id (spec 3.7). Immutable; built by <see cref="CombatRules"/>.</summary>
internal sealed class StatusCatalog
{
    private readonly Dictionary<string, StatusDefinition> _byId = new(StringComparer.Ordinal);

    private StatusCatalog()
    {
    }

    public static readonly StatusCatalog Empty = new();

    public int Count => _byId.Count;

    public bool TryGet(string id, out StatusDefinition definition) => _byId.TryGetValue(id, out definition!);

    /// <summary>Builds the catalog from rows that passed the validator; times round up to whole ticks (like action phases).</summary>
    public static StatusCatalog From(IReadOnlyList<StatusEffectDef> statuses, IReadOnlyList<StatusModifierDef> modifiers,
        CombatConfig config, Func<int, int> ticksFor)
    {
        var catalog = new StatusCatalog();
        foreach (var status in statuses)
        {
            var own = new List<StatusModifier>();
            foreach (var modifier in modifiers)
            {
                if (modifier.StatusId == status.Id)
                    own.Add(new StatusModifier(config.Stats.IdFor(modifier.Stat), modifier.Flat, modifier.Percent));
            }
            int interval = status.HasTicks() ? Math.Max(1, ticksFor(status.TickIntervalMs)) : 0;
            catalog._byId[status.Id] = new StatusDefinition(status, own.ToArray(), Math.Max(1, ticksFor(status.DurationMs)),
                interval, config.ElementIndex(status.TickElement));
        }
        return catalog;
    }
}
