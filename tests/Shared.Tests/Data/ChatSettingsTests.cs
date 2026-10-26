using Shared.Data;
using Shared.Data.Tables;

namespace Shared.Tests.Data;

public class ChatSettingsTests
{
    private static RowErrors Validate(ChatSettings row)
    {
        var errors = new RowErrors();
        row.Validate(errors);
        return errors;
    }

    private static ChatSettings Valid() =>
        new() { MaxChars = 200, MessagesPerPeriod = 3, PeriodMs = 5_000, Burst = 5, HistoryLines = 50 };

    [Fact]
    public void A_burst_below_the_per_period_rate_is_a_broken_rule()
    {
        var row = Valid();
        row.Burst = 2;

        Assert.Equal(nameof(ChatSettings.Burst), Assert.Single(Validate(row).Items).Field);
    }

    [Fact]
    public void A_burst_equal_to_the_rate_is_valid()
    {
        var row = Valid();
        row.Burst = row.MessagesPerPeriod;

        Assert.Empty(Validate(row).Items);
    }
}
