using Shared.Data;
using Shared.Data.Tables;

namespace GameServer.Combat;

/// <summary>
/// The projectile tables a world runs with (spec 3.2): <see cref="ProjectileDef"/> rows by id, the
/// <see cref="ProjectileSettings"/> limits, and the tick rate the paths are stepped at. Built once from validated tables.
/// </summary>
public sealed class ProjectileRules
{
    private readonly Dictionary<string, ProjectileDef> _byId = new(StringComparer.Ordinal);

    public ProjectileRules(IEnumerable<ProjectileDef> defs, ProjectileSettings settings, int tickRateHz)
    {
        if (tickRateHz <= 0)
            throw new ArgumentOutOfRangeException(nameof(tickRateHz), "must be positive");
        foreach (var def in defs)
            _byId.Add(def.Id, def);
        Settings = settings;
        TickRateHz = tickRateHz;
    }

    public ProjectileSettings Settings { get; }

    public int TickRateHz { get; }

    public IReadOnlyCollection<ProjectileDef> Defs => _byId.Values;

    public bool TryGet(string id, out ProjectileDef def) => _byId.TryGetValue(id, out def!);

    /// <summary>From tables that passed validation.</summary>
    public static ProjectileRules From(ConfigSet set)
    {
        var defs = new List<ProjectileDef>();
        if (set.TryGet(typeof(ProjectileDef), out var table))
        {
            foreach (var row in table.Rows)
                defs.Add((ProjectileDef)row);
        }
        return new ProjectileRules(defs, set.Single<ProjectileSettings>(), set.Single<StartingValuesM1>().TickRateHz);
    }
}
