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
//   --give <name> <item> [count]  with --dev, Debug builds only (repeatable): after migrating, gives that offline character
//                       the item (count 1 by default) once, like tools/qa/Set-BagItem.ps1 / Set-BagInstance.ps1 (DevItems; the
//                       load test's BotScenario.seedItemIds). A second run gives nothing more.
//   --mail <name> <item> [count] [gold]  with --dev, Debug builds only (repeatable): after migrating, writes that character a
//                       system mail with the item (count 1) and the gold (0) through MailWriter, the mail send path (DevMail;
//                       the QA smoke step claim-mail, WP-M4-3). A second run with the same arguments writes nothing more.
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
const string Usage = "Usage: DbMigrate [--dev [--db-suffix <suffix>] [--set-level <character name> <level>] [--give <character name> <item> [count]]... [--mail <character name> <item> [count] [gold]]...] [--create-databases]";

var dev = false;
var createDatabases = false;
string? suffix = null;
string? levelName = null;
int newLevel = 0;
var gives = new List<(string Name, string Item, int Count)>();
var mails = new List<(string Name, string Item, int Count, long Gold)>();
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
#if DEBUG
        case "--give" when i + 2 < args.Length:
        {
            int count = 1;
            bool counted = i + 3 < args.Length && Shared.Data.Tables.BotScenario.TryParseSeedCount(args[i + 3], out count);
            gives.Add((args[i + 1], args[i + 2], counted ? count : 1));
            i += counted ? 3 : 2;
            break;
        }
        case "--mail" when i + 2 < args.Length:
        {
            int count = 1;
            long gold = 0;
            int used = 2;
            if (i + 3 < args.Length && Shared.Data.Tables.BotScenario.TryParseSeedCount(args[i + 3], out count))
            {
                used = 3;
                if (i + 4 < args.Length && long.TryParse(args[i + 4], out var parsedGold) && parsedGold >= 0)
                {
                    gold = parsedGold;
                    used = 4;
                }
            }
            else
                count = 1;
            mails.Add((args[i + 1], args[i + 2], count, gold));
            i += used;
            break;
        }
#endif
        default:
            Console.Error.WriteLine($"Unknown argument: {arg}");
            Console.Error.WriteLine(Usage);
            return 2;
    }
}

if (mails.Count > 0 && !dev)
{
    Console.Error.WriteLine("--mail needs --dev: it writes the local dev database only.");
    Console.Error.WriteLine(Usage);
    return 2;
}

if (gives.Count > 0 && !dev)
{
    Console.Error.WriteLine("--give needs --dev: it writes the local dev database only.");
    Console.Error.WriteLine(Usage);
    return 2;
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

if (levelName != null && await DevLevel.SetAsync(realm!, levelName, newLevel) is var levelExit && levelExit != 0)
    return levelExit;

#if DEBUG
if (gives.Count > 0 && await DevItems.RunAsync(realm!, gives) is var giveExit && giveExit != 0)
    return giveExit;
if (mails.Count > 0)
    return await DevMail.RunAsync(realm!, mails);
#endif

return 0;
