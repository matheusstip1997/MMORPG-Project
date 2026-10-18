using GameServer.Host.Config;
using Shared.Data;
using Shared.Data.Tables;

namespace AuthApi;

/// <summary>The config tables the Auth API reads, loaded and validated once at startup (spec 1.8).</summary>
public sealed record AuthConfig(
    CharacterSlots Slots,
    CharacterCreationConfig Creation,
    AuthApiLimits Limits,
    NetSecurity NetSecurity,
    StartingValuesM1 StartingValues)
{
    /// <summary>Loads every table, as the game server does, so a broken table stops both.</summary>
    public static AuthConfig Load(string tablesDirectory)
    {
        var result = ConfigLoader.LoadAndValidate(tablesDirectory, ConfigTables.All);
        if (!result.Ok)
            throw new InvalidOperationException(
                $"Config tables in {Path.GetFullPath(tablesDirectory)} are invalid:{Environment.NewLine}" +
                string.Join(Environment.NewLine, result.Errors));

        var set = result.Set;
        return new AuthConfig(
            set.Single<CharacterSlots>(),
            set.Single<CharacterCreationConfig>(),
            set.Single<AuthApiLimits>(),
            set.Single<NetSecurity>(),
            set.Single<StartingValuesM1>());
    }
}
