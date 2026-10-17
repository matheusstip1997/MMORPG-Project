using Shared.Data;
using Shared.Data.Tables;

namespace Shared.Tests.Data;

public class TelemetrySettingsTests
{
    private static RowErrors Validate(TelemetrySettings row)
    {
        var errors = new RowErrors();
        row.Validate(errors);
        return errors;
    }

    [Fact]
    public void Queue_capacity_must_be_a_power_of_two()
    {
        var errors = Validate(new TelemetrySettings { QueueCapacity = 1_000, MaxBatchRecords = 100 });

        Assert.Equal(nameof(TelemetrySettings.QueueCapacity), Assert.Single(errors.Items).Field);
    }

    [Fact]
    public void Batch_larger_than_the_queue_is_a_broken_rule()
    {
        var errors = Validate(new TelemetrySettings { QueueCapacity = 1_024, MaxBatchRecords = 2_048 });

        Assert.Equal(nameof(TelemetrySettings.MaxBatchRecords), Assert.Single(errors.Items).Field);
    }

    [Fact]
    public void Power_of_two_queue_with_a_smaller_batch_is_valid()
    {
        Assert.Empty(Validate(new TelemetrySettings { QueueCapacity = 1_024, MaxBatchRecords = 1_024 }).Items);
    }
}
