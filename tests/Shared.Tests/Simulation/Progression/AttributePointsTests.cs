using Shared.Protocol;
using Shared.Simulation.Progression;
using Shared.Simulation.Stats;

namespace Shared.Tests.Simulation.Progression;

/// <summary>
/// The free attribute points of R-3.4-08 (WP-M2-13, user decisions 2026-10-29) as pure rules: one point per level from
/// level 2, and the checks of a placement in their order.
/// </summary>
public class AttributePointsTests
{
    [Theory]
    [InlineData(1, 1, 0)]
    [InlineData(2, 1, 1)]
    [InlineData(30, 1, 29)]
    [InlineData(30, 2, 58)]
    [InlineData(0, 1, 0)]
    [InlineData(10, 0, 0)]
    public void Points_come_from_level_2_on(int level, int perLevel, int expected) =>
        Assert.Equal(expected, AttributePoints.Earned(level, perLevel));

    [Fact]
    public void A_placement_with_a_point_out_of_combat_below_the_max_is_allowed() =>
        Assert.Equal(AttributeReason.None, AttributePoints.Check(true, Attributes.Str, false, 1, 10, 100_000));

    [Fact]
    public void The_checks_come_in_their_order()
    {
        // Not loaded beats everything; then an unknown attribute; then combat; then no point; then the max.
        Assert.Equal(AttributeReason.AttributeUnavailable, AttributePoints.Check(false, 9, true, 0, 100_000, 100_000));
        Assert.Equal(AttributeReason.UnknownAttribute, AttributePoints.Check(true, Attributes.Count, true, 0, 100_000, 100_000));
        Assert.Equal(AttributeReason.UnknownAttribute, AttributePoints.Check(true, -1, false, 1, 0, 100_000));
        Assert.Equal(AttributeReason.AttributeBlockedCombat, AttributePoints.Check(true, Attributes.Int, true, 0, 100_000, 100_000));
        Assert.Equal(AttributeReason.NoAttributePoints, AttributePoints.Check(true, Attributes.Dex, false, 0, 100_000, 100_000));
        Assert.Equal(AttributeReason.AttributeAtMax, AttributePoints.Check(true, Attributes.Sta, false, 3, 100_000, 100_000));
    }

    [Fact]
    public void There_is_no_cap_below_the_stat_max()
    {
        // User decision 2026-10-29: no cap per attribute in M2 beyond the StatDef max.
        Assert.Equal(AttributeReason.None, AttributePoints.Check(true, Attributes.Str, false, 1, 99_999, 100_000));
        Assert.Equal(AttributeReason.AttributeAtMax, AttributePoints.Check(true, Attributes.Str, false, 1, 100_000, 100_000));
    }
}
