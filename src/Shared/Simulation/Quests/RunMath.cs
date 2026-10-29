using System;

namespace Shared.Simulation.Quests
{
    /// <summary>
    /// The exp of outpost runs (R-5.5-02, R-5.5-03; spec 5.5 "M2 parameters", user decisions 2026-10-27), integer math only (rule
    /// 6). Shared so the client's run builder previews what the server locks. All results round down; an overflow throws instead of wrapping.
    /// </summary>
    public static class RunMath
    {
        /// <summary>
        /// The reward of one step, locked at acceptance: <paramref name="baseExp"/> (the monster's) x
        /// <paramref name="expPercentPerUnit"/> % x <paramref name="count"/> x <paramref name="rewardPercent"/> % (the demand
        /// reward multiplier, R-4.2-03).
        /// </summary>
        public static long StepReward(int baseExp, int expPercentPerUnit, int count, int rewardPercent)
        {
            if (baseExp < 0 || expPercentPerUnit < 0 || count < 0 || rewardPercent < 0)
                throw new ArgumentOutOfRangeException(nameof(baseExp), "inputs must not be negative");
            return checked((long)baseExp * expPercentPerUnit * count * rewardPercent / 10_000);
        }

        /// <summary>A demand-scaled count: <paramref name="count"/> x <paramref name="rewardPercent"/> %, clamped to min..max.</summary>
        public static int ScaledCount(int count, int rewardPercent, int min, int max)
        {
            long scaled = (long)count * rewardPercent / 100;
            return (int)Math.Max(min, Math.Min(max, scaled));
        }

        /// <summary>The step bonus: <paramref name="perStepPercent"/> per completed step beyond the first, capped (<c>RunBonus</c>).</summary>
        public static int BonusPercent(int completedSteps, int perStepPercent, int capPercent) =>
            completedSteps <= 1 ? 0 : (int)Math.Min((long)(completedSteps - 1) * perStepPercent, capPercent);

        /// <summary>The run's penalty: <paramref name="perForfeitPercent"/> per forfeited step, capped (<c>ForfeitPenalty</c>).</summary>
        public static int PenaltyPercent(int forfeitedSteps, int perForfeitPercent, int capPercent) =>
            forfeitedSteps <= 0 ? 0 : (int)Math.Min((long)forfeitedSteps * perForfeitPercent, Math.Min(capPercent, 100));

        /// <summary>
        /// What a claim pays (R-5.5-03, one claim): the locked rewards of the completed steps x (100 + bonus) % x (100 - penalty) %.
        /// Forfeited steps pay nothing, so the caller leaves them out of <paramref name="completedRewards"/>.
        /// </summary>
        public static long ClaimExp(long completedRewards, int bonusPercent, int penaltyPercent)
        {
            if (completedRewards < 0 || bonusPercent < 0 || penaltyPercent < 0 || penaltyPercent > 100)
                throw new ArgumentOutOfRangeException(nameof(completedRewards), "rewards and percents out of range");
            return checked(completedRewards * (100 + bonusPercent) * (100 - penaltyPercent) / 10_000);
        }
    }
}
