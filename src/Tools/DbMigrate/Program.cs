// Applies the schema migrations (R-7.1-04) to the global and the realm database (R-2.1-03, R-7.1-03).
// Usage: dotnet run --project src/Tools/DbMigrate -- [--dev [--db-suffix <suffix>]] [--create-databases]
//   default             connection strings from MMORPG_GLOBAL_DB and MMORPG_REALM_DB
//   --dev               the local docker-compose server: databases mmorpg_global and mmorpg_realm_1,
//                       created if missing (implies --create-databases). Local dev only.
//   --db-suffix <s>     with --dev: databases mmorpg_global_<s> and mmorpg_realm_1_<s> instead, so sessions that share
//                       one machine each migrate their own dev databases (1 to 32 lowercase letters, digits, '_')
//   --create-databases  create the target databases first if they do not exist (dev and test only)
//   --set-level <name> <level>  with --dev: after migrating, sets that character's level (exp 0) in the realm database and
//                       adds ProgressionSettings.skillPointsPerLevel points per level gained (data/tables), for play tests of
//                       the skill tree (WP-M2-5: a level-30 character to advance). Log the character out first: the row's
//                       version moves, so the save of a world that still holds it fails instead of writing over it.
// Exit codes: 0 up to date, 1 migration failed, 2 bad arguments or missing configuration.

using System.Text.RegularExpressions;
using GameServer.Host;
using GameServer.Persistence;
using GameServer.Persistence.Migrations;
using Npgsql;

// Dev-only defaults, the same as docker-compose.yml. Never used unless --dev is passed.
const string DevServer = "Host=127.0.0.1;Port=5432;Username=mmorpg;Password=mmorpg_dev";
const string DevGlobalDatabase = "mmorpg_global";
const string DevRealmDatabase = "mmorpg_realm_1";
const string Usage = "Usage: DbMigrate [--dev [--db-suffix <suffix>] [--set-level <character name> <level>]] [--create-databases]";

var dev = false;
var createDatabases = false;
string? suffix = null;
string? levelName = null;
int newLevel = 0;
for (int i = 0; i < args.Length; i++)
{
    var arg = args[i];
    switch (arg)
    {
        case "--dev":
            dev = true;
            createDatabases = true;
            break;
        case "--create-databases":
            createDatabases = true;
            break;
        case "--db-suffix" when i + 1 < args.Length:
            suffix = args[++i];
            break;
        case "--set-level" when i + 2 < args.Length && int.TryParse(args[i + 2], out var parsed) && parsed >= 1:
            levelName = args[i + 1];
            newLevel = parsed;
            i += 2;
            break;
        default:
            Console.Error.WriteLine($"Unknown argument: {arg}");
            Console.Error.WriteLine(Usage);
            return 2;
    }
}

if (levelName != null && !dev)
{
    Console.Error.WriteLine("--set-level needs --dev: it writes the local dev database only.");
    Console.Error.WriteLine(Usage);
    return 2;
}

if (suffix != null && (!dev || !Regex.IsMatch(suffix, "^[a-z0-9_]{1,32}$")))
{
    Console.Error.WriteLine("--db-suffix needs --dev and 1 to 32 lowercase letters, digits or '_'.");
    Console.Error.WriteLine(Usage);
    return 2;
}

string? global, realm;
if (dev)
{
    var tail = suffix == null ? "" : "_" + suffix;
    global = $"{DevServer};Database={DevGlobalDatabase}{tail}";
    realm = $"{DevServer};Database={DevRealmDatabase}{tail}";
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

if (levelName != null)
    return await DevLevel.SetAsync(realm!, levelName, newLevel);

return 0;
