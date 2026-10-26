using AdminPanel.Logs;
using AdminPanel.Queries;
using GameServer.Admin;
using Shared.Data;
using Shared.Data.Tables;

namespace AdminPanel.Tests;

public sealed class AccountQueryTests
{
    [Theory]
    [InlineData("dev:7", 7L, null)]
    [InlineData("76561198000000001", 76561198000000001L, null)]
    [InlineData(" id:12 ", null, 12L)]
    public void Accepts_a_dev_login_a_SteamID_or_an_account_id(string text, long? steamId, long? accountId)
    {
        Assert.True(AccountQuery.TryParse(text, out var query));
        Assert.Equal(new AccountQuery(steamId, accountId), query);
    }

    [Theory]
    [InlineData("")]
    [InlineData("dev:")]
    [InlineData("dev:-1")]
    [InlineData("id:0")]
    [InlineData("steam:5")]
    [InlineData("1; DROP TABLE accounts")]
    public void Refuses_anything_else(string text) => Assert.False(AccountQuery.TryParse(text, out _));
}

public sealed class LogTailTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("logtail-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void Reads_the_last_lines_of_the_newest_file_with_a_filter()
    {
        File.WriteAllLines(Path.Combine(_directory, "old.log"), ["old"]);
        File.SetLastWriteTimeUtc(Path.Combine(_directory, "old.log"), DateTime.UtcNow.AddHours(-1));
        File.WriteAllLines(Path.Combine(_directory, "gameserver.log"), ["a 1", "b 2", "a 3", "a 4"]);

        var result = LogTail.Read(_directory, file: null, contains: "A", maxLines: 2, maxBytes: 1_024);

        Assert.Equal(["gameserver.log", "old.log"], result.Files);
        Assert.Equal(["a 3", "a 4"], result.Lines);
    }

    [Fact]
    public void Reads_at_most_the_byte_cap_and_drops_the_cut_line()
    {
        File.WriteAllText(Path.Combine(_directory, "x.log"), "first line\nsecond line\nthird\n");

        var result = LogTail.Read(_directory, "x.log", null, maxLines: 10, maxBytes: 10);

        Assert.Equal(["third"], result.Lines);
    }

    [Fact]
    public void Names_that_are_not_listed_files_read_nothing()
    {
        File.WriteAllText(Path.Combine(_directory, "x.log"), "secret\n");

        Assert.Empty(LogTail.Read(_directory, "../x.log", null, 10, 1_024).Lines);
        Assert.Empty(LogTail.Read(_directory, Path.Combine(_directory, "x.log"), null, 10, 1_024).Lines);
        Assert.Empty(LogTail.Read(Path.Combine(_directory, "missing"), null, null, 10, 1_024).Files);
    }
}

public sealed class TelemetryTailTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("telemetrytail-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void Returns_matching_records_newest_first_across_files_and_skips_headers_and_partial_lines()
    {
        var older = Path.Combine(_directory, "run-0000.jsonl");
        File.WriteAllText(older, "{\"schema\":\"s\",\"v\":1}\n{\"k\":\"Moved\",\"t\":1,\"c\":5}\n");
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddMinutes(-5));
        File.WriteAllText(Path.Combine(_directory, "run-0001.jsonl"),
            "{\"k\":\"Damaged\",\"t\":2,\"c\":5}\n{\"k\":\"Moved\",\"t\":3,\"c\":6}\n{\"k\":\"Moved\",\"t\":4,\"c\":5");

        var all = TelemetryTail.Read(_directory, new TelemetryFilter(5, null, null), maxRecords: 10, maxBytes: 10_000);
        var moved = TelemetryTail.Read(_directory, new TelemetryFilter(null, null, "Moved"), 10, 10_000);

        Assert.Equal([2L, 1L], all.Select(r => r.GetProperty("t").GetInt64()));
        Assert.Equal([3L, 1L], moved.Select(r => r.GetProperty("t").GetInt64()));
        Assert.Single(TelemetryTail.Read(_directory, new TelemetryFilter(5, null, null), maxRecords: 1, 10_000));
    }
}

public sealed class AdminSettingsTests
{
    private static RowErrors Validate(AdminSettings row)
    {
        var errors = new RowErrors();
        row.Validate(errors);
        return errors;
    }

    [Fact]
    public void A_command_must_get_several_polls_before_it_expires()
    {
        Assert.Equal(nameof(AdminSettings.CommandExpiryMs),
            Assert.Single(Validate(new AdminSettings { CommandPollMs = 1_000, CommandExpiryMs = 3_999 }).Items).Field);
        Assert.Empty(Validate(new AdminSettings { CommandPollMs = 1_000, CommandExpiryMs = 4_000 }).Items);
    }
}

#if DEV_LOGIN
public sealed class DevAdminIdentityTests
{
    [Theory]
    [InlineData("ana", AdminReasonCode.None, "dev:ana")]
    [InlineData(" ops.team-1 ", AdminReasonCode.None, "dev:ops.team-1")]
    [InlineData("", AdminReasonCode.RequestInvalid, "")]
    [InlineData("a b", AdminReasonCode.RequestInvalid, "")]
    [InlineData("<script>", AdminReasonCode.RequestInvalid, "")]
    [InlineData("averyveryverylongname", AdminReasonCode.RequestInvalid, "")]
    public void Any_plain_name_signs_in_as_dev_name(string name, AdminReasonCode reason, string adminName)
    {
        Assert.Equal(reason, new Identity.Dev.DevAdminIdentity().Verify(name, maxChars: 20, out var actual));
        Assert.Equal(adminName, actual);
    }
}
#endif
