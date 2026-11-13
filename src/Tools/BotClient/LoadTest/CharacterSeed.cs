using System.Diagnostics;
using System.Globalization;
using BotClient.Login;
using Game.UI.Auth;
using Shared.Data.Tables;
using Shared.Util;

namespace BotClient.LoadTest;

/// <summary>
/// The level seed (<c>BotScenario.seedLevel</c>, WP-M3-12; moved here by WP-M4-12 so <c>run</c> seeds the QA partner bot
/// the same way): each bot's character is created through the Auth API without entering the world (nothing lingers or
/// saves over the seed), then the built DbMigrate sets its level (<c>--dev [--db-suffix] --set-level</c>, the dev
/// databases only), gives it the seed items (<c>--give</c>, once per character) and writes its seed mail (<c>--mail</c>, WP-M4-14). The bots' own login finds the same
/// character (the name stream is the one <c>BotRunner</c> uses). Writes are through DbMigrate only, never raw SQL.
/// </summary>
public static class CharacterSeed
{
    private static readonly TimeSpan SeedTimeout = TimeSpan.FromSeconds(120);

    public static void Apply(string repository, string configuration, string dbSuffix, string runDirectory, IAuthHttp http,
        string authUrl, int realmId, int bots, IReadOnlyList<string> classes, long accountBase, ulong seed, int level,
        IReadOnlyList<string> items, Action<string> log, IReadOnlyList<string>? mails = null, string mailTag = "")
    {
        string dll = Path.Combine(repository, "artifacts", "bin", "DbMigrate", configuration, "DbMigrate.dll");
        if (!File.Exists(dll))
            throw new FileNotFoundException($"the level seed needs a built DbMigrate ({dll}); build src/Tools/DbMigrate first");
        Directory.CreateDirectory(runDirectory);
        using var seedLog = new StreamWriter(Path.Combine(runDirectory, "seed.log"), append: false);
        for (int i = 0; i < bots; i++)
        {
            var rng = new Pcg32Rng(seed + 1, (ulong)i); // the stream BotRunner names the characters with
            string name = BotLogin.EnsureCharacterAsync(http, authUrl, realmId, accountBase + i, classes[i % classes.Count], rng).GetAwaiter().GetResult();
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = repository, // DbMigrate reads data/tables for ProgressionSettings
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var argument in new[] { dll, "--dev" })
                start.ArgumentList.Add(argument);
            if (dbSuffix.Length > 0)
            {
                start.ArgumentList.Add("--db-suffix");
                start.ArgumentList.Add(dbSuffix);
            }
            foreach (var argument in new[] { "--set-level", name, level.ToString(CultureInfo.InvariantCulture) })
                start.ArgumentList.Add(argument);
            foreach (var entry in items) // BotScenario.seedItemIds (wave 1 QA F6): given once per character, Debug DbMigrate only
            {
                if (!BotScenario.TryParseSeedItem(entry, out string item, out int count))
                    throw new ArgumentException($"seed item '{entry}' is not <itemId> or <itemId>:<count>");
                foreach (var argument in new[] { "--give", name, item, count.ToString(CultureInfo.InvariantCulture) })
                    start.ArgumentList.Add(argument);
            }
            foreach (var argument in MailArguments(name, mails ?? Array.Empty<string>(), mailTag))
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("DbMigrate did not start");
            var stderr = process.StandardError.ReadToEndAsync();
            string stdout = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(SeedTimeout))
            {
                process.Kill(entireProcessTree: true);
                throw new InvalidOperationException($"DbMigrate --set-level {name} did not finish within {SeedTimeout.TotalSeconds} s");
            }
            seedLog.Write(stdout);
            seedLog.Write(stderr.Result);
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"DbMigrate --set-level {name} {level} exited with {process.ExitCode}: {stderr.Result.Trim()} (seed.log)");
            log($"bot {i}: {name} seeded to level {level}" + (items.Count > 0 ? ", items " + string.Join(", ", items) : "")
                + (mails is { Count: > 0 } ? ", mail " + string.Join(", ", mails) : ""));
        }
    }

    /// <summary>
    /// The DbMigrate arguments of <c>BotScenario.seedMail</c> (WP-M4-14): <c>--mail &lt;name&gt; &lt;item&gt; &lt;count&gt;
    /// &lt;gold&gt;</c> per entry (one system mail each, Debug DbMigrate only) and <c>--mail-tag</c> with the run's tag, so another
    /// run writes the mails again (the dev mail's operation key is per character, item, count, gold and tag).
    /// </summary>
    public static IReadOnlyList<string> MailArguments(string characterName, IReadOnlyList<string> mails, string tag)
    {
        var arguments = new List<string>();
        foreach (var entry in mails)
        {
            if (!BotScenario.TryParseSeedMail(entry, out string item, out int count, out long gold))
                throw new ArgumentException($"seed mail '{entry}' is not <itemId>:<count>:<gold>");
            arguments.AddRange(["--mail", characterName, item, count.ToString(CultureInfo.InvariantCulture),
                gold.ToString(CultureInfo.InvariantCulture)]);
        }
        if (arguments.Count > 0 && tag.Length > 0)
            arguments.AddRange(["--mail-tag", tag]);
        return arguments;
    }

    /// <summary>A <c>--mail-tag</c> for a run: its folder name and start time, reduced to what DbMigrate accepts.</summary>
    public static string MailTag(string runId, DateTime startedUtc)
    {
        string tag = new string($"{runId}-{startedUtc:yyyyMMddHHmmss}".Where(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-').ToArray());
        return tag.Length > 64 ? tag.Substring(tag.Length - 64) : tag;
    }
}
