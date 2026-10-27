using BotClient.LoadTest;

namespace GameServer.Tests.Bots;

/// <summary>WP-M2-12: the M2 exit run's level report (spec 12.2: a new character from level 1 to about 30).</summary>
public class LevelReportTests
{
    [Fact]
    public void Without_a_level_in_the_protocol_the_report_says_so_and_the_criterion_fails()
    {
        var report = new LevelReport(bots: 5, targetLevel: 30, levelInProtocol: false);
        report.Observe(0, new int?[5], keep: true);
        report.Observe(60_000, new int?[5], keep: true);

        Assert.Equal(["level per bot: level not in protocol yet (no own-entity level message; WP-M2-3)"], report.Lines());
        var criterion = report.Criterion(14_400_000);
        Assert.False(criterion.Pass);
        Assert.Equal(("bots reached level 30", "5 of 5 within 14400 s", "level not in protocol yet"),
            (criterion.Name, criterion.Expected, criterion.Actual));
    }

    [Fact]
    public void Levels_over_time_give_the_table_the_time_each_bot_reached_the_target_and_the_verdict()
    {
        var report = new LevelReport(bots: 2, targetLevel: 30, levelInProtocol: true);
        report.Observe(0, [1, 1], keep: true);
        report.Observe(61_000, [12, 9], keep: false); // not a sample, but bot 0 is not there yet either
        report.Observe(120_000, [30, 17], keep: true);
        Assert.False(report.AllReached);
        var failing = report.Criterion(600_000);
        Assert.Equal(("1 of 2 (levels 30, 17)", false), (failing.Actual, failing.Pass));

        report.Observe(185_500, [31, 30], keep: true);
        Assert.True(report.AllReached);
        Assert.Equal([120L, 185L], report.ReachedAtSeconds);
        Assert.True(report.Criterion(600_000).Pass);
        var lines = report.Lines().ToList();
        Assert.Equal(5, lines.Count); // header, three samples, who reached it
        Assert.Contains("bot1", lines[0], StringComparison.Ordinal);
        Assert.Equal("level 30: bot0 at 120 s, bot1 at 185 s", lines[^1]);
    }

    [Fact]
    public void An_unknown_level_of_one_bot_shows_as_a_question_mark()
    {
        var report = new LevelReport(bots: 2, targetLevel: 30, levelInProtocol: true);
        report.Observe(0, [4, null], keep: true);
        Assert.Contains("    ?", report.Lines().ElementAt(1), StringComparison.Ordinal);
        Assert.Equal("0 of 2 (levels 4, ?)", report.Criterion(1_000).Actual);
        Assert.Equal("level 30: bot0 not reached (level 4), bot1 not reached (level ?)", report.Lines().Last());
    }
}
