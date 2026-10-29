using Shared.Data;
using Shared.Data.Tables;

namespace Shared.Tests.Data;

/// <summary>
/// The target lock's table (R-2.2-06, user decision 2026-10-26): <see cref="TargetLockSettings"/> ranges and its set rule
/// (break range between the acquire range and the interest radius, the <see cref="Hotkeys"/> row <c>targetLock</c>).
/// </summary>
public class TargetLockSettingsTests
{
    private static TargetLockSettings Row(int windowMs = 300, int acquireCm = 1500, int breakCm = 2000) => new()
    {
        DoublePressWindowMs = windowMs, AcquireRangeCm = acquireCm, BreakRangeCm = breakCm, CycleOrder = TargetCycleOrder.Distance,
    };

    private static ConfigSet Set(TargetLockSettings row, string hotkeyId = Hotkeys.TargetLock, int interestRadiusCm = 3000)
    {
        var set = new ConfigSet();
        set.Add(new ConfigTable(typeof(TargetLockSettings), 1, new object[] { row }));
        set.Add(new ConfigTable(typeof(Hotkeys), 1, new object[] { new Hotkeys { Id = hotkeyId, Binding = "<Keyboard>/tab" } }));
        set.Add(new ConfigTable(typeof(StartingValuesM1), 1, new object[] { new StartingValuesM1 { InterestRadiusCm = interestRadiusCm } }));
        return set;
    }

    // Only this table's errors: the partial StartingValuesM1 row breaks its own ranges.
    private static List<string> Errors(ConfigSet set) =>
        TableValidator.Validate(set).Where(e => e.Table == "TargetLockSettings").Select(e => e.Field + ": " + e.Message).ToList();

    [Fact]
    public void The_placeholder_row_with_its_key_passes() => Assert.Empty(Errors(Set(Row())));

    [Theory]
    [InlineData(50, 1500, 2000)]
    [InlineData(1500, 1500, 2000)]
    [InlineData(300, 0, 2000)]
    [InlineData(300, 1500, 200_000)]
    public void Values_out_of_range_are_refused(int windowMs, int acquireCm, int breakCm)
    {
        Assert.NotEmpty(Errors(Set(Row(windowMs, acquireCm, breakCm), interestRadiusCm: 100_000)));
    }

    [Fact]
    public void A_break_range_below_the_acquire_range_is_refused()
    {
        var errors = Errors(Set(Row(acquireCm: 1500, breakCm: 1000)));

        Assert.Contains(errors, e => e.StartsWith("BreakRangeCm") && e.Contains("AcquireRangeCm"));
    }

    [Fact]
    public void A_break_range_beyond_the_interest_radius_is_refused()
    {
        var errors = Errors(Set(Row(breakCm: 3500)));

        Assert.Contains(errors, e => e.StartsWith("BreakRangeCm") && e.Contains("InterestRadiusCm"));
    }

    [Fact]
    public void The_break_range_may_equal_the_acquire_range_and_the_interest_radius()
    {
        Assert.Empty(Errors(Set(Row(acquireCm: 3000, breakCm: 3000))));
    }

    [Fact]
    public void The_lock_needs_its_hotkey_row()
    {
        var errors = Errors(Set(Row(), hotkeyId: "other"));

        Assert.Contains(errors, e => e.Contains(Hotkeys.TargetLock));
    }

    [Fact]
    public void The_table_is_registered_and_client_only()
    {
        Assert.Contains(typeof(TargetLockSettings), ConfigTables.All);
        Assert.Equal(ReloadClass.NotServer, TableReloadPolicy.ClassOf(typeof(TargetLockSettings)));
    }
}
