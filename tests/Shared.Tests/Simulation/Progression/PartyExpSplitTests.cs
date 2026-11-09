using Shared.Simulation.Progression;

namespace Shared.Tests.Simulation.Progression;

/// <summary>
/// The party share of a kill's exp (spec 6.5 "M4 party", R-3.6-03; M4 decision 1; WP-M4-2) as pure functions: the weight of
/// each eligible member is its damage share raised to the floor, the weights scaled to one; a member's exp is its own gain
/// times its weight, rounded down.
/// </summary>
public class PartyExpSplitTests
{
    private static long[] Weights(int floorPercent, params long[] damage)
    {
        var weights = new long[damage.Length];
        PartyExpSplit.Weights(damage, floorPercent, weights);
        return weights;
    }

    [Fact]
    public void A_party_of_one_gets_the_whole_gain()
    {
        Assert.Equal([ExpMath.PpmOne], Weights(10, 500));
        Assert.Equal([ExpMath.PpmOne], Weights(10, 0));
        Assert.Equal(123, PartyExpSplit.Share(123, ExpMath.PpmOne));
    }

    [Fact]
    public void Without_a_floor_the_weights_follow_the_damage()
    {
        Assert.Equal([750_000L, 250_000L], Weights(0, 300, 100));
    }

    [Fact]
    public void The_floor_lifts_a_member_who_dealt_little_and_the_weights_still_sum_to_one()
    {
        // 990 and 10 damage: raw 990 000 and 10 000 ppm, the floor lifts the second to 100 000, then both scale by 1 090 000.
        var weights = Weights(10, 990, 10);

        Assert.Equal([908_256L, 91_743L], weights);
        Assert.True(weights.Sum() <= ExpMath.PpmOne);
        Assert.True(weights[1] > 10_000);
    }

    [Fact]
    public void A_member_in_range_with_no_damage_still_gets_the_floor_share()
    {
        var weights = Weights(10, 1_000, 0, 0);

        Assert.All(weights[1..], w => Assert.Equal(weights[1], w));
        Assert.True(weights[1] > 0);
        Assert.True(weights[0] > weights[1]);
    }

    [Fact]
    public void No_damage_at_all_shares_evenly()
    {
        Assert.Equal([333_333L, 333_333L, 333_333L], Weights(0, 0, 0, 0));
    }

    [Fact]
    public void Six_members_at_the_floor_never_get_more_than_the_whole()
    {
        var weights = Weights(30, 6_000, 1, 1, 1, 1, 1);

        Assert.True(weights.Sum() <= ExpMath.PpmOne);
        Assert.Equal(6, weights.Length);
        Assert.All(weights[1..], w => Assert.Equal(weights[1], w));
    }

    [Fact]
    public void A_share_rounds_down_and_is_zero_for_no_gain_or_no_weight()
    {
        Assert.Equal(33, PartyExpSplit.Share(100, 333_333));
        Assert.Equal(0, PartyExpSplit.Share(0, 500_000));
        Assert.Equal(0, PartyExpSplit.Share(100, 0));
    }

    [Fact]
    public void Bad_inputs_throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Weights(101, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Weights(10, -1));
        Assert.Throws<ArgumentException>(() => PartyExpSplit.Weights(new long[] { 1, 2 }, 10, new long[1]));
    }
}
