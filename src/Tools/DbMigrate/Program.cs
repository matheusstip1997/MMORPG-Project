// Applies the schema migrations (R-7.1-04) to the global and the realm database (R-2.1-03, R-7.1-03).
// Usage: dotnet run --project src/Tools/DbMigrate -- [--dev] [--create-databases]
//   default             connection strings from MMORPG_GLOBAL_DB and MMORPG_REALM_DB
//   --dev               the local docker-compose server: databases mmorpg_global and mmorpg_realm_1,
//                       created if missing (implies --create-databases). Local dev only.
//   --create-databases  create the target databases first if they do not exist (dev and test only)
// Exit codes: 0 up to date, 1 migration failed, 2 bad arguments or missing configuration.

using GameServer.Host;
using GameServer.Persistence;
using GameServer.Persistence.Migrations;
using Npgsql;

// Dev-only defaults, the same as docker-compose.yml. Never used unless --dev is passed.
const string DevServer = "Host=127.0.0.1;Port=5432;Username=mmorpg;Password=mmorpg_dev";
const string DevGlobalDatabase = "mmorpg_global";
const string DevRealmDatabase = "mmorpg_realm_1";

var dev = false;
var createDatabases = false;
foreach (var arg in args)
{
    switch (arg)
    {
        case "--dev":
            dev = true;
            createDatabases = true;
            break;
        case "--create-databases":
            createDatabases = true;
            break;
        default:
            Console.Error.WriteLine($"Unknown argument: {arg}");
            Console.Error.WriteLine("Usage: DbMigrate [--dev] [--create-databases]");
            return 2;
    }
}

string? global, realm;
if (dev)
{
    global = $"{DevServer};Database={DevGlobalDatabase}";
    realm = $"{DevServer};Database={DevRealmDatabase}";
}
else
{
    global = Environment.GetEnvironmentVariable(PersistenceSettings.GlobalVariable);
    realm = Environment.GetEnvironmentVariable(PersistenceSettings.RealmVariable);
    if (string.IsNullOrWhiteSpace(global) || string.IsNullOrWhiteSpace(realm))
    {
        Console.Error.WriteLine($"Set {PersistenceSettings.GlobalVariable} and {PersistenceSettings.RealmVariable}, or pass --dev for the local docker-compose server.");
        return 2;
    }
}

var runner = new MigrationRunner(new SystemClock());
try
{
    foreach (var (kind, connectionString) in new[] { (DatabaseKind.Global, global), (DatabaseKind.Realm, realm) })
    {
        var name = DatabaseAdmin.DatabaseNameOf(connectionString);
        if (createDatabases && await DatabaseAdmin.EnsureDatabaseAsync(connectionString, name))
            Console.WriteLine($"{kind}: created database {name}");

        await using var source = NpgsqlDataSource.Create(connectionString);
        var report = await runner.ApplyAsync(source, MigrationCatalog.For(kind));
        var applied = report.Applied.Count == 0 ? "nothing to apply" : "applied " + string.Join(", ", report.Applied.Select(v => $"V{v:D4}"));
        Console.WriteLine($"{kind} ({name}): {applied}; schema at V{report.CurrentVersion:D4}");
    }
}
catch (Exception e) when (e is MigrationException or NpgsqlException)
{
    Console.Error.WriteLine($"Migration failed: {e.Message}");
    return 1;
}

return 0;
