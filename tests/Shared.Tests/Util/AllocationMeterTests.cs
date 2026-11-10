using Xunit.Sdk;

namespace Shared.Tests.Util;

/// <summary>
/// The meter every allocation test uses (tests/Common/AllocationMeter.cs) passes a window that allocates nothing and fails
/// one that allocates, including an allocation the code makes only once.
/// </summary>
public class AllocationMeterTests
{
    private static int[] _sink = Array.Empty<int>();

    [Fact]
    public void A_window_that_allocates_nothing_passes()
    {
        int sum = 0;
        AllocationMeter.AssertNone(() =>
        {
            for (int i = 0; i < 5_000; i++)
                sum += i; // its loop may be compiled inside the window: no bytes, so it passes
        });
        Assert.Equal(12_497_500, sum);
    }

    [Fact]
    public void A_single_small_allocation_fails()
    {
        var failure = Assert.Throws<TrueException>(() => AllocationMeter.AssertNone(() => _sink = new int[1]));
        Assert.Contains("bytes in the window", failure.Message);
    }

    [Fact]
    public void An_allocation_only_in_the_first_run_fails_and_the_window_runs_once()
    {
        int runs = 0;
        Assert.Throws<TrueException>(() => AllocationMeter.AssertNone(() =>
        {
            if (++runs == 1)
                _sink = new int[1]; // a one-shot path: a list created on a first, rare branch
        }));
        Assert.Equal(1, runs);
    }

    [Fact]
    public void Measure_reports_the_window_s_bytes()
    {
        var measured = AllocationMeter.Measure(() => _sink = new int[100]);
        Assert.True(measured.Bytes >= 400, measured.ToString());
    }
}
