using Shared.Data.Tables;
using Shared.Simulation.Stats;

namespace GameServer.Combat;

/// <summary>One build-up change of a hit (<see cref="EffectBuildUpDef"/>): the build-up's slot and the points (negative lowers).</summary>
internal readonly record struct BuildUpChange(int Index, int Amount);

/// <summary>
/// Every build-up of the config (spec 3.8, <see cref="BuildUpDef"/>) with its triggered status, and the build-up changes of
/// every effect that has them (<see cref="EffectBuildUpDef"/>), built once by <see cref="CombatRules"/> from rows that passed
/// the validator. Build-ups are sorted by id; each combatant holds one <see cref="BuildUpMeter"/> per build-up in that order
/// (deterministic, rule 6). Immutable.
/// </summary>
internal sealed class BuildUpCatalog
{
    private readonly BuildUpRule[] _rules;
    private readonly StatusDefinition[] _statuses;
    private readonly Dictionary<string, BuildUpChange[]> _changesByEffect;

    private BuildUpCatalog(BuildUpRule[] rules, StatusDefinition[] statuses, Dictionary<string, BuildUpChange[]> changesByEffect)
    {
        _rules = rules;
        _statuses = statuses;
        _changesByEffect = changesByEffect;
    }

    public static readonly BuildUpCatalog Empty = new([], [], new Dictionary<string, BuildUpChange[]>(StringComparer.Ordinal));

    /// <summary>Build-ups, and so meters per combatant.</summary>
    public int Count => _rules.Length;

    public BuildUpRule this[int index] => _rules[index];

    /// <summary>The status the build-up at <paramref name="index"/> triggers (spec 3.7).</summary>
    public StatusDefinition StatusOf(int index) => _statuses[index];

    /// <summary>The build-up changes of an effect's hits, in build-up order; false when it has none.</summary>
    public bool TryGetChanges(string effectId, out BuildUpChange[] changes) => _changesByEffect.TryGetValue(effectId, out changes!);

    /// <summary>The slot of a build-up id, or -1.</summary>
    public int IndexOf(string id)
    {
        for (int i = 0; i < _rules.Length; i++)
        {
            if (string.Equals(_rules[i].Id, id, StringComparison.Ordinal))
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Builds the catalog. A build-up whose status is not in <paramref name="statuses"/> or that has no element is left out
    /// (the validator refuses such rows), and so are the changes naming it.
    /// </summary>
    public static BuildUpCatalog From(IReadOnlyList<BuildUpDef> buildUps, IReadOnlyList<EffectBuildUpDef> changes,
        StatusCatalog statuses, CombatConfig config, Func<int, int> ticksFor, int tickRateHz)
    {
        var rules = new List<BuildUpRule>();
        var triggered = new List<StatusDefinition>();
        foreach (var def in buildUps.OrderBy(b => b.Id, StringComparer.Ordinal))
        {
            int element = config.ElementIndex(def.Element);
            if (!statuses.TryGet(def.StatusId, out var status) || element == DamageCalc.NoElement)
                continue;
            rules.Add(new BuildUpRule(def, rules.Count, element, config.ElementResistStat(element), ticksFor(def.DecayDelayMs),
                ticksFor(def.RepeatPenaltyWindowMs), tickRateHz));
            triggered.Add(status);
        }
        var byEffect = new Dictionary<string, List<BuildUpChange>>(StringComparer.Ordinal);
        foreach (var change in changes)
        {
            int index = rules.FindIndex(r => r.Id == change.BuildUpId);
            if (index < 0)
                continue;
            if (!byEffect.TryGetValue(change.EffectId, out var list))
                byEffect.Add(change.EffectId, list = []);
            list.Add(new BuildUpChange(index, change.Amount));
        }
        var frozen = new Dictionary<string, BuildUpChange[]>(StringComparer.Ordinal);
        foreach (var (effectId, list) in byEffect)
            frozen.Add(effectId, list.OrderBy(c => c.Index).ToArray());
        return new BuildUpCatalog(rules.ToArray(), triggered.ToArray(), frozen);
    }
}
