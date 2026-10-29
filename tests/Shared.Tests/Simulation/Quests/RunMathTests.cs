using Shared.Simulation.Quests;

namespace Shared.Tests.Simulation.Quests;

/// <summary>
/// The exp of outpost runs as pure functions (R-5.5-02, R-5.5-03; spec 5.5 "M2 parameters", user decisions 2026-10-27, the
/// placeholders of <c>RunBonus</c> 10/50 and <c>ForfeitPenalty</c> 20/100): the step bonus counts completed steps only, each
/// forfeited step takes 20 % off the run, a claim rounds down once.
/// </summary>
public class RunMathTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, 10)]
    [InlineData(3, 20)]
    [InlineData(5, 40)]
    [InlineData(6, 50)]
    [InlineData(8, 50)] // capped at +50 %
    public void The_step_bonus_is_ten_percent_per_completed_step_beyond_the_first_capped_at_fifty(int completed, int expected)
    {
        Assert.Equal(expected, RunMath.BonusPercent(completed, perStepPercent: 10, capPercent: 50));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 20)]
    [InlineData(2, 40)]
    [InlineData(5, 100)]
    [InlineData(6, 100)] // capped
    public void Each_forfeited_step_takes_twenty_percent_off_the_run(int forfeited, int expected)
    {
        Assert.Equal(expected, RunMath.PenaltyPercent(forfeited, perForfeitPercent: 20, capPercent: 100));
    }

    [Fact]
    public void The_penalty_never_passes_one_hundred_percent_even_with_a_larger_cap()
    {
        Assert.Equal(100, RunMath.PenaltyPercent(9, perForfeitPercent: 50, capPercent: 100));
    }

    [Fact]
    public void A_four_step_run_with_one_forfeit_pays_its_three_steps_with_the_bonus_then_the_penalty()
    {
        // The user's example (2026-10-27): 3 done steps -> +20 %, 1 forfeit -> -20 %; the forfeited step's 500 pays nothing.
        long completedRewards = 100 + 200 + 300;
        int bonus = RunMath.BonusPercent(3, 10, 50);
        int penalty = RunMath.PenaltyPercent(1, 20, 100);

        Assert.Equal(600 * 120 * 80 / 10_000, RunMath.ClaimExp(completedRewards, bonus, penalty));
        Assert.Equal(576, RunMath.ClaimExp(completedRewards, bonus, penalty));
    }

    [Fact]
    public void A_two_step_run_without_forfeits_pays_ten_percent_more_than_its_steps()
    {
        Assert.Equal(330, RunMath.ClaimExp(300, RunMath.BonusPercent(2, 10, 50), 0));
    }

    [Fact]
    public void A_claim_rounds_down_once_not_per_step()
    {
        // 7 x 1.1 x 0.8 = 6.16 -> 6
        Assert.Equal(6, RunMath.ClaimExp(7, 10, 20));
        Assert.Equal(0, RunMath.ClaimExp(1, 0, 100));
    }

    [Theory]
    [InlineData(40, 50, 10, 100, 200)]   // 40 exp x 50 % x 10 units x 100 % demand
    [InlineData(40, 50, 10, 150, 300)]   // rarely hunted: x1.5
    [InlineData(40, 50, 10, 50, 100)]    // heavily hunted: x0.5
    [InlineData(33, 40, 3, 100, 39)]     // 39.6 rounds down
    [InlineData(0, 50, 10, 100, 0)]
    public void A_step_reward_is_base_exp_times_percent_per_unit_times_count_times_demand(int baseExp, int perUnit, int count,
        int rewardPercent, long expected)
    {
        Assert.Equal(expected, RunMath.StepReward(baseExp, perUnit, count, rewardPercent));
    }

    [Theory]
    [InlineData(10, 100, 10)]
    [InlineData(10, 150, 15)]
    [InlineData(10, 300, 20)]  // clamped to countMax
    [InlineData(10, 30, 5)]    // clamped to countMin
    [InlineData(7, 110, 7)]    // 7.7 rounds down
    public void A_demand_scaled_count_follows_the_reward_percent_inside_its_range(int count, int rewardPercent, int expected)
    {
        Assert.Equal(expected, RunMath.ScaledCount(count, rewardPercent, min: 5, max: 20));
    }

    [Fact]
    public void Overflow_and_negative_inputs_throw_instead_of_wrapping()
    {
        Assert.Throws<OverflowException>(() => RunMath.StepReward(int.MaxValue, 100_000, 9_999, 1_000));
        Assert.Throws<OverflowException>(() => RunMath.ClaimExp(long.MaxValue / 2, 50, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => RunMath.StepReward(-1, 50, 1, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => RunMath.ClaimExp(10, 0, 101));
    }
}
