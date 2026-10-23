using GameServer.Combat;
using GameServer.Events;
using Shared.Data.Tables;
using Shared.Protocol;

namespace GameServer.Tests.Combat;

/// <summary>Spec 3.3 ParrySystem as a pure function: colors (R-3.3-04), armor classes (R-3.3-05), timing and charges (R-3.3-03).</summary>
public class ParrySystemTests
{
    [Theory]
    // Uncolored: a parry reduces the damage, whatever the armor.
    [InlineData(ParryColor.None, ArmorClass.Light, ParryResult.Reduced)]
    [InlineData(ParryColor.None, ArmorClass.Medium, ParryResult.Reduced)]
    [InlineData(ParryColor.None, ArmorClass.Heavy, ParryResult.Reduced)]
    // Red: nobody.
    [InlineData(ParryColor.Red, ArmorClass.Light, ParryResult.FailedRed)]
    [InlineData(ParryColor.Red, ArmorClass.Medium, ParryResult.FailedRed)]
    [InlineData(ParryColor.Red, ArmorClass.Heavy, ParryResult.FailedRed)]
    // Yellow: heavy armor only.
    [InlineData(ParryColor.Yellow, ArmorClass.Light, ParryResult.FailedArmor)]
    [InlineData(ParryColor.Yellow, ArmorClass.Medium, ParryResult.FailedArmor)]
    [InlineData(ParryColor.Yellow, ArmorClass.Heavy, ParryResult.Success)]
    // Blue: medium or heavy armor.
    [InlineData(ParryColor.Blue, ArmorClass.Light, ParryResult.FailedArmor)]
    [InlineData(ParryColor.Blue, ArmorClass.Medium, ParryResult.Success)]
    [InlineData(ParryColor.Blue, ArmorClass.Heavy, ParryResult.Success)]
    // Green: anyone.
    [InlineData(ParryColor.Green, ArmorClass.Light, ParryResult.Success)]
    [InlineData(ParryColor.Green, ArmorClass.Medium, ParryResult.Success)]
    [InlineData(ParryColor.Green, ArmorClass.Heavy, ParryResult.Success)]
    public void A_parry_in_its_window_is_judged_by_color_and_armor_class(ParryColor color, ArmorClass armor, ParryResult expected) =>
        Assert.Equal(expected, ParrySystem.Judge(color, armor, inWindow: true, noCharge: false));

    [Theory]
    [InlineData(ParryColor.None)]
    [InlineData(ParryColor.Green)]
    [InlineData(ParryColor.Red)]
    public void Outside_the_window_every_parry_fails_on_timing(ParryColor color) =>
        Assert.Equal(ParryResult.FailedTiming, ParrySystem.Judge(color, ArmorClass.Heavy, inWindow: false, noCharge: false));

    [Theory]
    [InlineData(ParryColor.None)]
    [InlineData(ParryColor.Green)]
    public void A_parry_refused_for_want_of_a_charge_reports_no_charge(ParryColor color) =>
        Assert.Equal(ParryResult.NoCharge, ParrySystem.Judge(color, ArmorClass.Heavy, inWindow: true, noCharge: true));

    [Theory]
    [InlineData(100, 50, 50)]
    [InlineData(15, 50, 7)] // rounds down
    [InlineData(10, 0, 10)]
    [InlineData(10, 100, 0)]
    [InlineData(10, 150, 0)] // clamped
    public void The_uncolored_reduction_takes_the_config_percent_off(int value, int percent, int expected) =>
        Assert.Equal(expected, ParrySystem.Reduce(value, percent));

    [Fact]
    public void Every_result_has_its_reason_code_for_the_client()
    {
        Assert.Equal(ActionReason.ParrySuccess, ParrySystem.ReasonFor(ParryResult.Success));
        Assert.Equal(ActionReason.ParryReduced, ParrySystem.ReasonFor(ParryResult.Reduced));
        Assert.Equal(ActionReason.ParryFailedRed, ParrySystem.ReasonFor(ParryResult.FailedRed));
        Assert.Equal(ActionReason.ParryFailedArmor, ParrySystem.ReasonFor(ParryResult.FailedArmor));
        Assert.Equal(ActionReason.ParryFailedTiming, ParrySystem.ReasonFor(ParryResult.FailedTiming));
        Assert.Equal(ActionReason.ParryNoCharge, ParrySystem.ReasonFor(ParryResult.NoCharge));
    }

    [Fact]
    public void Colors_and_results_keep_their_numbers()
    {
        // Append only: snapshots carry the color in 3 bits, telemetry stores both.
        Assert.Equal(4, (int)ParryColor.Green);
        Assert.True((int)ParryColor.Green < 1 << Snapshot.ParryColorBits);
        Assert.Equal(6, (int)ParryResult.NoCharge);
        Assert.Equal(4, (int)ParryResult.FailedArmor);
    }
}
