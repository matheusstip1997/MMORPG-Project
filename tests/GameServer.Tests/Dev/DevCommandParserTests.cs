using GameServer.Dev.Commands;
using Shared.Protocol;

namespace GameServer.Tests.Dev;

/// <summary>The dev console's line parser (spec 11.7 dev console, WP-M3-13): shapes only; ids and limits are the executor's.</summary>
public class DevCommandParserTests
{
    [Theory]
    [InlineData("/give m3.oreT1 4", DevCommandKind.Give, 2)]
    [InlineData("/GIVE m3.oreT1", DevCommandKind.Give, 1)]
    [InlineData("/level 16", DevCommandKind.Level, 1)]
    [InlineData("/gold 500", DevCommandKind.Gold, 1)]
    [InlineData("/job blacksmith", DevCommandKind.Job, 1)]
    [InlineData("/job jeweler secondary", DevCommandKind.Job, 2)]
    [InlineData("/joblevel blacksmith 10", DevCommandKind.JobLevel, 2)]
    [InlineData("/kills m2.wolf 14", DevCommandKind.Kills, 2)]
    [InlineData("/tp field1.spawn.wolf.c1", DevCommandKind.Teleport, 1)]
    [InlineData("/tp 100 -250", DevCommandKind.Teleport, 2)]
    [InlineData("/heal", DevCommandKind.Heal, 0)]
    [InlineData("/die", DevCommandKind.Die, 0)]
    [InlineData("/spawn m2.wolf 3", DevCommandKind.Spawn, 2)]
    [InlineData("/node deplete", DevCommandKind.Node, 1)]
    [InlineData("/where", DevCommandKind.Where, 0)]
    [InlineData("/help", DevCommandKind.Help, 0)]
    [InlineData("/help give", DevCommandKind.Help, 1)]
    [InlineData("/clearbag", DevCommandKind.ClearBag, 0)]
    [InlineData("/take m3.oreT1  2", DevCommandKind.Take, 2)]
    [InlineData("/exp 1000", DevCommandKind.Exp, 1)]
    [InlineData("/points skill 5", DevCommandKind.Points, 2)]
    public void Every_command_parses_with_its_arguments(string line, DevCommandKind kind, int args)
    {
        Assert.Equal(DevReason.None, DevCommandParser.TryParse(line, out var command, out _));
        Assert.Equal(kind, command!.Kind);
        Assert.Equal(args, command.Args.Count);
    }

    [Fact]
    public void Options_are_key_value_words_the_command_knows()
    {
        Assert.Equal(DevReason.None, DevCommandParser.TryParse("/give m3.forgedSword rarity=rare enchant=3 gems=m3.rubyT1", out var command, out _));
        Assert.Equal(("rare", "3", "m3.rubyT1"), (command!.Option("rarity"), command.Option("enchant"), command.Option("gems")));
        Assert.Single(command.Args);

        Assert.Equal(DevReason.BadArguments, DevCommandParser.TryParse("/give m3.oreT1 color=red", out _, out var detail));
        Assert.Contains("usage:", detail);
        Assert.Equal(DevReason.BadArguments, DevCommandParser.TryParse("/give m3.oreT1 enchant=", out _, out _));
        Assert.Equal(DevReason.BadArguments, DevCommandParser.TryParse("/give m3.forgedSword enchant=1 enchant=2", out _, out _));
        Assert.Equal(DevReason.BadArguments, DevCommandParser.TryParse("/level 3 rarity=rare", out _, out _)); // not a give option
    }

    [Theory]
    [InlineData("/give")]
    [InlineData("/give a 1 2")]
    [InlineData("/level")]
    [InlineData("/heal now")]
    [InlineData("/tp a b c")]
    [InlineData("/points skill")]
    public void A_wrong_argument_count_is_refused_with_the_usage(string line)
    {
        Assert.Equal(DevReason.BadArguments, DevCommandParser.TryParse(line, out var command, out var detail));
        Assert.Null(command);
        Assert.StartsWith("usage: /", detail);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/fly")]
    [InlineData("hello")]
    public void An_unknown_command_is_refused(string line)
    {
        Assert.Equal(DevReason.UnknownCommand, DevCommandParser.TryParse(line, out _, out var detail));
        Assert.NotEmpty(detail);
    }

    [Fact]
    public void Help_lists_every_command_within_the_protocol_caps()
    {
        var all = DevCommandParser.HelpLines(null);
        Assert.Equal(DevCommandParser.Commands.Count, all.Count);
        Assert.True(all.Count <= DevConsoleLimits.MaxLines);
        Assert.All(all, line => Assert.True(line.Length <= DevConsoleLimits.MaxLineChars, line));
        Assert.Equal(["/level <n>", "levels up to n, with the points of each level"], DevCommandParser.HelpLines("/level"));
        Assert.StartsWith("no command", DevCommandParser.HelpLines("fly")[0]);
    }

    [Fact]
    public void Command_names_are_unique_and_every_kind_has_one()
    {
        Assert.Equal(DevCommandParser.Commands.Count, DevCommandParser.Commands.Select(c => c.Name).Distinct().Count());
        foreach (var kind in Enum.GetValues<DevCommandKind>().Where(k => k != DevCommandKind.None))
            Assert.Single(DevCommandParser.Commands, c => c.Kind == kind);
    }

    [Theory]
    [InlineData("12", 1, 20, true, 12)]
    [InlineData("0", 1, 20, false, 0)]
    [InlineData("21", 1, 20, false, 21)]
    [InlineData("-5", -10, 10, true, -5)]
    [InlineData("1.5", 0, 10, false, 0)]
    [InlineData("1e3", 0, 10000, false, 0)]
    [InlineData("99999999999999999999", 0, long.MaxValue, false, 0)]
    public void Numbers_are_whole_and_in_range(string text, long min, long max, bool ok, long value)
    {
        Assert.Equal(ok, DevCommandParser.TryInt(text, min, max, out long parsed));
        if (ok)
            Assert.Equal(value, parsed);
    }

    [Fact]
    public void A_dev_SteamId_is_the_dev_logins_small_number_never_a_real_SteamId64()
    {
        Assert.True(DevAccountDirectory.IsDevSteamId(7));
        Assert.False(DevAccountDirectory.IsDevSteamId(0));
        Assert.False(DevAccountDirectory.IsDevSteamId(76561197960265728)); // the first individual Steam account
        Assert.False(DevAccountDirectory.IsDevSteamId(76561198000000000));
    }

    [Fact]
    public async Task The_account_directory_looks_each_account_up_once_off_the_world_thread()
    {
        int lookups = 0;
        var gate = new TaskCompletionSource<long?>();
        var directory = new DevAccountDirectory(async (id, _) =>
        {
            Interlocked.Increment(ref lookups);
            return id == 1 ? await gate.Task : 76561198000000001;
        });

        Assert.Equal(DevAccountStatus.Checking, directory.Status(1)); // never waits
        Assert.Equal(DevAccountStatus.Checking, directory.Status(1));
        gate.SetResult(7);
        await WaitFor(() => directory.Status(1) == DevAccountStatus.Dev);
        directory.Prefetch(2);
        await WaitFor(() => directory.Status(2) == DevAccountStatus.NotDev);
        Assert.Equal(2, lookups);
    }

    [Fact]
    public async Task A_failed_lookup_is_tried_again()
    {
        int calls = 0;
        var lines = new List<string>();
        var directory = new DevAccountDirectory((_, _) => ++calls == 1 ? throw new TimeoutException("db down") : Task.FromResult<long?>(3),
            line => { lock (lines) lines.Add(line); });

        directory.Prefetch(5);
        await WaitFor(() => calls == 1 && directory.Status(5) != DevAccountStatus.NotDev);
        await WaitFor(() => directory.Status(5) == DevAccountStatus.Dev);
        Assert.Single(lines);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition());
    }
}
