namespace GameServer.Persistence;

/// <summary>
/// Where the databases are (R-2.1-03, R-7.1-03): one global database and one database per realm.
/// Connection strings come from the environment and are never committed. For local dev against
/// docker-compose, see the values in CLAUDE.md (dev only).
/// </summary>
public sealed record PersistenceSettings(string GlobalConnectionString, string RealmConnectionString, int RealmId)
{
    public const string GlobalVariable = "MMORPG_GLOBAL_DB";
    public const string RealmVariable = "MMORPG_REALM_DB";
    public const string RealmIdVariable = "MMORPG_REALM_ID";

    public static PersistenceSettings FromEnvironment() => From(Environment.GetEnvironmentVariable);

    public static PersistenceSettings From(Func<string, string?> read)
    {
        var global = Require(read, GlobalVariable);
        var realm = Require(read, RealmVariable);
        var realmIdText = Require(read, RealmIdVariable);
        if (!int.TryParse(realmIdText, out var realmId) || realmId < 1)
            throw new InvalidOperationException($"{RealmIdVariable} must be a positive integer, got '{realmIdText}'");
        return new PersistenceSettings(global, realm, realmId);
    }

    private static string Require(Func<string, string?> read, string name)
    {
        var value = read(name);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"environment variable {name} is not set");
        return value;
    }
}
