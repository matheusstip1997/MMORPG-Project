using System.Globalization;
using Shared.Protocol;

namespace GameServer.Dev.Commands;

/// <summary>The dev console's commands (spec 11.7, WP-M3-13). Stored in telemetry (<c>DevCommand</c> target): append only.</summary>
public enum DevCommandKind : byte
{
    None = 0,
    Help = 1,
    Give = 2,
    Take = 3,
    ClearBag = 4,
    Gold = 5,
    Level = 6,
    Exp = 7,
    Points = 8,
    Job = 9,
    JobLevel = 10,
    Kills = 11,
    Teleport = 12,
    Heal = 13,
    Die = 14,
    Spawn = 15,
    Node = 16,
    Where = 17,
}

/// <summary>
/// One command of the help table: its name, how many positional arguments it takes, the <c>key=value</c> options it knows,
/// its usage line and what it does. Tooling, so it lives in code (the limits come from the game's tables, WP-M3-13 plan).
/// </summary>
public sealed record DevCommandSpec(DevCommandKind Kind, string Name, int MinArgs, int MaxArgs, string[] Options, string Usage, string Help);

/// <summary>A parsed command line: the command, its positional arguments in order and its options by key.</summary>
public sealed record DevCommand(DevCommandSpec Spec, IReadOnlyList<string> Args, IReadOnlyDictionary<string, string> Options)
{
    public DevCommandKind Kind => Spec.Kind;

    public string Arg(int index) => index < Args.Count ? Args[index] : string.Empty;

    public string? Option(string key) => Options.TryGetValue(key, out var value) ? value : null;
}

/// <summary>
/// Reads a dev console line (spec 11.7, WP-M3-13): a chat line that starts with <c>/</c>, the command name (any case), then
/// arguments separated by white space; an argument of the form <c>key=value</c> is an option. Pure and allocation-light:
/// it runs only for dev lines, never in a hot path. It checks the shape only (the count of arguments and the known
/// options); ids and numbers are checked against the tables by the executor.
/// </summary>
public static class DevCommandParser
{
    public const char Prefix = '/';

    public static readonly IReadOnlyList<DevCommandSpec> Commands = new[]
    {
        new DevCommandSpec(DevCommandKind.Help, "help", 0, 1, Array.Empty<string>(), "/help [command]", "lists the commands, or one command's usage"),
        new DevCommandSpec(DevCommandKind.Give, "give", 1, 2, new[] { "rarity", "enchant", "gems" },
            "/give <itemId> [count] [rarity=<rarityId>] [enchant=<n>] [gems=<gemId,...>]", "adds items where loot goes: materials to the Normal bag, gear and tools to the Safe bag (options: gear)"),
        new DevCommandSpec(DevCommandKind.Take, "take", 1, 2, Array.Empty<string>(), "/take <itemId> [count]", "removes items from the bag that holds them (the Normal or the Safe bag; never equipped gear)"),
        new DevCommandSpec(DevCommandKind.ClearBag, "clearbag", 0, 0, Array.Empty<string>(), "/clearbag", "empties the Normal bag (equipped gear stays)"),
        new DevCommandSpec(DevCommandKind.Gold, "gold", 1, 1, Array.Empty<string>(), "/gold <amount>", "sets the gold to the amount"),
        new DevCommandSpec(DevCommandKind.Level, "level", 1, 1, Array.Empty<string>(), "/level <n>", "levels up to n, with the points of each level"),
        new DevCommandSpec(DevCommandKind.Exp, "exp", 1, 1, Array.Empty<string>(), "/exp <amount>", "gains exp as from kills"),
        new DevCommandSpec(DevCommandKind.Points, "points", 2, 2, Array.Empty<string>(), "/points <skill|attr> <n>", "adds unspent skill or attribute points"),
        new DevCommandSpec(DevCommandKind.Job, "job", 1, 2, Array.Empty<string>(), "/job <jobId> [main|secondary]", "takes a job (its starter items too)"),
        new DevCommandSpec(DevCommandKind.JobLevel, "joblevel", 2, 2, Array.Empty<string>(), "/joblevel <jobId> <level>", "levels a held job up, with its points"),
        new DevCommandSpec(DevCommandKind.Kills, "kills", 2, 2, Array.Empty<string>(), "/kills <monsterId> <count>", "raises a kill count (the Bystial book follows)"),
        new DevCommandSpec(DevCommandKind.Teleport, "tp", 1, 2, Array.Empty<string>(), "/tp <markerId> | /tp <xCm> <yCm>", "moves you to a marker or a point"),
        new DevCommandSpec(DevCommandKind.Heal, "heal", 0, 0, Array.Empty<string>(), "/heal", "fills HP, MP, stamina and poise"),
        new DevCommandSpec(DevCommandKind.Die, "die", 0, 0, Array.Empty<string>(), "/die", "kills you (respawn as in play)"),
        new DevCommandSpec(DevCommandKind.Spawn, "spawn", 1, 2, Array.Empty<string>(), "/spawn <monsterId> [count]", "spawns monsters at you (no respawn)"),
        new DevCommandSpec(DevCommandKind.Node, "node", 1, 1, Array.Empty<string>(), "/node <deplete|respawn>", "depletes or respawns the nearest resource node"),
        new DevCommandSpec(DevCommandKind.Where, "where", 0, 0, Array.Empty<string>(), "/where", "shows your position, region and the nearest marker"),
    };

    /// <summary>A chat line meant for the dev console (it never reaches local chat).</summary>
    public static bool IsCommand(string? line) => line != null && line.Length > 0 && line[0] == Prefix;

    public static DevCommandSpec? Find(string name)
    {
        foreach (var spec in Commands)
        {
            if (string.Equals(spec.Name, name, StringComparison.OrdinalIgnoreCase))
                return spec;
        }
        return null;
    }

    /// <summary>
    /// Parses <paramref name="line"/> (with its <c>/</c>). <see cref="DevReason.None"/> and the command, or
    /// <see cref="DevReason.UnknownCommand"/> / <see cref="DevReason.BadArguments"/> with the line to show in
    /// <paramref name="detail"/>.
    /// </summary>
    public static DevReason TryParse(string line, out DevCommand? command, out string detail)
    {
        command = null;
        detail = string.Empty;
        if (!IsCommand(line))
        {
            detail = "a command starts with /";
            return DevReason.UnknownCommand;
        }
        var words = line.Substring(1).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || Find(words[0]) is not { } spec)
        {
            detail = words.Length == 0 ? "type /help" : $"no command '{Clip(words[0])}': type /help";
            return DevReason.UnknownCommand;
        }
        var args = new List<string>();
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < words.Length; i++)
        {
            int equals = words[i].IndexOf('=');
            if (equals < 0)
            {
                args.Add(words[i]);
                continue;
            }
            string key = words[i].Substring(0, equals);
            string value = words[i].Substring(equals + 1);
            if (Array.FindIndex(spec.Options, o => string.Equals(o, key, StringComparison.OrdinalIgnoreCase)) < 0 || value.Length == 0
                || options.ContainsKey(key))
            {
                detail = $"bad option '{Clip(words[i])}'; usage: {spec.Usage}";
                return DevReason.BadArguments;
            }
            options[key] = value;
        }
        if (args.Count < spec.MinArgs || args.Count > spec.MaxArgs)
        {
            detail = "usage: " + spec.Usage;
            return DevReason.BadArguments;
        }
        command = new DevCommand(spec, args, options);
        return DevReason.None;
    }

    /// <summary>A whole number in <paramref name="min"/>..<paramref name="max"/> (invariant digits, an optional minus sign).</summary>
    public static bool TryInt(string text, long min, long max, out long value) =>
        long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value) && value >= min && value <= max;

    /// <summary>The help lines: every command's usage, or one command's usage and what it does.</summary>
    public static List<string> HelpLines(string? name)
    {
        var lines = new List<string>();
        if (!string.IsNullOrEmpty(name))
        {
            if (Find(name!.TrimStart(Prefix)) is { } one)
            {
                lines.Add(one.Usage);
                lines.Add(one.Help);
                return lines;
            }
            lines.Add($"no command '{Clip(name)}'");
        }
        foreach (var spec in Commands)
            lines.Add(spec.Usage);
        return lines;
    }

    /// <summary>A user word inside a detail line, kept short so the line stays within the protocol cap.</summary>
    public static string Clip(string text) => text.Length <= 40 ? text : text.Substring(0, 40) + "...";
}
