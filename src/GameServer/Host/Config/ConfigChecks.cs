using GameServer.Combat;
using Shared.Data;
using Shared.Data.Tables;

namespace GameServer.Host.Config;

/// <summary>
/// Every check the config tables must pass, in one place, so <c>DataValidator</c>, the server start and the dev hot reload
/// (spec 11.7) accept exactly the same tables: the loader and <see cref="TableValidator"/> (schema, ranges, ids,
/// references, row and set rules), the coverage rules (every reason code enum and every name the HUD shows has a text
/// row, spec 9.1; every reliable message a client sends a <see cref="ReliableMessageCaps"/> row, R-2.5-07), then the
/// cross-table rules of the stat, damage, class and monster tables (<see cref="CombatRules.TryBuild"/>, which runs
/// <c>CombatConfig.TryBuild</c>), which assume rows that passed everything before.
/// </summary>
public static class ConfigChecks
{
    /// <summary>Loads <paramref name="tablesDirectory"/> and runs every check.</summary>
    public static ConfigLoadResult LoadAndCheck(string tablesDirectory) =>
        Check(ConfigLoader.LoadAndValidate(tablesDirectory, ConfigTables.All));

    /// <summary>Adds the coverage and cross-table checks to a <see cref="ConfigLoader.LoadAndValidate"/> result.</summary>
    public static ConfigLoadResult Check(ConfigLoadResult loaded)
    {
        var errors = loaded.Errors.Concat(ReasonCodeCoverage.Check(loaded.Set)).Concat(UiStringCoverage.Check(loaded.Set))
            .Concat(ReliableMessageCaps.CheckCoverage(loaded.Set)).Concat(EconomyCoverage.Check(loaded.Set)).ToList();
        if (errors.Count == 0)
            CombatRules.TryBuild(loaded.Set, errors);
        return loaded with { Errors = errors };
    }
}
