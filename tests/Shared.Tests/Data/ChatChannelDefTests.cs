using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;

namespace Shared.Tests.Data;

/// <summary>The per-channel chat rate limits (WP-M4-2; ChatSettings v1's burst rule moved here).</summary>
public class ChatChannelDefTests
{
    private static RowErrors Validate(ChatChannelDef row)
    {
        var errors = new RowErrors();
        row.Validate(errors);
        return errors;
    }

    private static ChatChannelDef Valid(string channel = "local") =>
        new() { Channel = channel, MessagesPerPeriod = 3, PeriodMs = 5_000, Burst = 5 };

    [Fact]
    public void A_burst_below_the_per_period_rate_is_a_broken_rule()
    {
        var row = Valid();
        row.Burst = 2;

        Assert.Equal(nameof(ChatChannelDef.Burst), Assert.Single(Validate(row).Items).Field);
    }

    [Fact]
    public void A_burst_equal_to_the_rate_is_valid()
    {
        var row = Valid();
        row.Burst = row.MessagesPerPeriod;

        Assert.Empty(Validate(row).Items);
    }

    [Theory]
    [InlineData("local", ChatChannel.Local)]
    [InlineData("party", ChatChannel.Party)]
    [InlineData("whisper", ChatChannel.Whisper)]
    public void Every_channel_parses_from_its_camel_case_name(string id, ChatChannel channel)
    {
        Assert.True(ChatChannelDef.TryParse(id, out var parsed));
        Assert.Equal(channel, parsed);
        Assert.Empty(Validate(Valid(id)).Items);
    }

    [Fact]
    public void An_unknown_channel_is_a_broken_rule()
    {
        Assert.Equal(nameof(ChatChannelDef.Channel), Assert.Single(Validate(Valid("guild")).Items).Field);
    }

    [Fact]
    public void The_repository_rows_cover_every_channel_once()
    {
        var set = LoadRepository();

        Assert.Empty(ChatChannelDef.CheckCoverage(set));
        var rows = ChatChannelDef.ByChannel(set);
        Assert.Equal((int)ChatCodec.LastChannel + 1, rows.Length);
        Assert.All(rows, row => Assert.NotNull(row));
    }

    /// <summary>The committed data/tables, read with the client's portable reader and validated.</summary>
    private static ConfigSet LoadRepository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Mmorpg.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var tables = Path.Combine(directory!.FullName, "data", "tables");
        var errors = new List<ValidationError>();
        var set = ConfigTableReader.LoadAndValidate(ConfigTables.All, name =>
        {
            var path = Path.Combine(tables, name + ".json");
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }, errors);
        Assert.Empty(errors);
        return set;
    }
}
