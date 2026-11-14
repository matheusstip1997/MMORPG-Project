using System;
using Shared.Util;

namespace Shared.Simulation.Items
{
    /// <summary>
    /// The party loot roll (spec 4.3 R-4.3-02, "M4 party loot", M4 decision 2; WP-M4-6), pure and in integers (rule 6). A
    /// member's <b>loot debt</b> is the value of what it won (gold, <c>PriceBase</c>), decaying by half every
    /// <c>PartyLootParams.debtHalfLifeMs</c>. A unit goes to one candidate drawn with weights that fall as the debt grows
    /// (<see cref="RollWeights"/>); a stack is rolled unit by unit, each won unit adding its value to the winner's debt before the
    /// next draw (<see cref="RollUnits"/>). A roll draws from its own generator seeded by <see cref="NextSeed"/> from the world's
    /// loot stream, so the logged seed with the debts reproduces it.
    /// </summary>
    public static class PartyLoot
    {
        /// <summary>The weight of a member with no debt (parts per million).</summary>
        public const long WeightOne = 1_000_000;

        /// <summary>The stream of a roll's own generator (<see cref="Pcg32Rng"/>), next to the seed it is logged with.</summary>
        public const ulong RollStream = 0x504C4F4F54UL; // "PLOOT"

        /// <summary>The most candidates a roll takes (the protocol's party cap).</summary>
        public const int MaxCandidates = Protocol.ReliableMessages.MaxPartyMembers;

        /// <summary>The largest half-weight debt (<c>PartyLootParams.debtHalfWeightGold</c>): <c>WeightOne</c> times it fits a long.</summary>
        public const long MaxHalfWeightDebt = 1_000_000_000;

        /// <summary>
        /// The debt <paramref name="elapsedMs"/> after it was <paramref name="debt"/>: halved for each whole half-life, then
        /// lowered linearly inside the current half-life (from 1 to 1/2 of what is left), so it is exact at every half-life,
        /// never rises and never goes below 0.
        /// </summary>
        public static long DecayDebt(long debt, long elapsedMs, int halfLifeMs)
        {
            if (debt <= 0)
                return 0;
            if (elapsedMs <= 0)
                return debt;
            if (halfLifeMs <= 0)
                throw new ArgumentOutOfRangeException(nameof(halfLifeMs), "must be positive");
            long halves = elapsedMs / halfLifeMs;
            if (halves >= 62)
                return 0;
            long left = debt >> (int)halves;
            long rest = elapsedMs - halves * halfLifeMs;
            // left * rest / (2 * halfLife) without overflow: left fits in 62 bits only for huge debts, so split it.
            long cut = left / (2L * halfLifeMs) * rest + left % (2L * halfLifeMs) * rest / (2L * halfLifeMs);
            return left - cut;
        }

        /// <summary>
        /// The candidates' weights for one unit worth <paramref name="unitValue"/> gold: <c>WeightOne * H / (H + debt)</c> with
        /// <c>H</c> = <paramref name="halfWeightDebt"/> (the debt at which a member weighs half a member without debt), at least
        /// 1. A unit without value (0) changes no debt, so every candidate weighs the same.
        /// </summary>
        public static void RollWeights(ReadOnlySpan<long> debts, long unitValue, long halfWeightDebt, Span<long> weights)
        {
            if (weights.Length < debts.Length)
                throw new ArgumentException("weights is shorter than debts", nameof(weights));
            for (int i = 0; i < debts.Length; i++)
            {
                if (unitValue <= 0 || halfWeightDebt <= 0)
                {
                    weights[i] = WeightOne;
                    continue;
                }
                if (halfWeightDebt > MaxHalfWeightDebt)
                    throw new ArgumentOutOfRangeException(nameof(halfWeightDebt), "above MaxHalfWeightDebt");
                long debt = Math.Min(Math.Max(0, debts[i]), long.MaxValue - halfWeightDebt);
                weights[i] = Math.Max(1, WeightOne * halfWeightDebt / (halfWeightDebt + debt));
            }
        }

        /// <summary>The index of the candidate one draw picks, by <paramref name="weights"/> (each at least 1).</summary>
        public static int Pick(ReadOnlySpan<long> weights, IRng rng)
        {
            long total = 0;
            for (int i = 0; i < weights.Length; i++)
                total += weights[i];
            if (weights.Length == 0 || total <= 0 || total > int.MaxValue)
                throw new ArgumentException("weights must be positive and sum within an int", nameof(weights));
            int draw = rng.NextInt((int)total);
            for (int i = 0; i < weights.Length; i++)
            {
                draw -= (int)weights[i];
                if (draw < 0)
                    return i;
            }
            return weights.Length - 1;
        }

        /// <summary>A roll's seed: two draws of the world's loot stream (logged with the roll).</summary>
        public static ulong NextSeed(IRng lootStream) => ((ulong)lootStream.NextUInt() << 32) | lootStream.NextUInt();

        /// <summary>
        /// Rolls <paramref name="units"/> units among the candidates whose current (decayed) debts are <paramref name="debts"/>:
        /// each unit is drawn with <see cref="RollWeights"/> from the debts as they stand, then adds <paramref name="unitValue"/>
        /// to its winner's debt. Writes the units won per candidate to <paramref name="won"/>, the debts after the roll to
        /// <paramref name="debtsAfter"/> and the first unit's weights to <paramref name="firstWeights"/> (what the roll is logged
        /// with). The same seed and debts give the same result on every runtime.
        /// </summary>
        public static void RollUnits(ReadOnlySpan<long> debts, long unitValue, long halfWeightDebt, int units, ulong seed,
            Span<long> firstWeights, Span<int> won, Span<long> debtsAfter)
        {
            int n = debts.Length;
            if (n == 0 || n > MaxCandidates)
                throw new ArgumentOutOfRangeException(nameof(debts), "a roll takes 1 to MaxCandidates candidates");
            if (units <= 0)
                throw new ArgumentOutOfRangeException(nameof(units), "must be positive");
            if (firstWeights.Length < n || won.Length < n || debtsAfter.Length < n)
                throw new ArgumentException("output spans are shorter than debts");
            Span<long> weights = stackalloc long[MaxCandidates];
            weights = weights.Slice(0, n);
            for (int i = 0; i < n; i++)
            {
                won[i] = 0;
                debtsAfter[i] = Math.Max(0, debts[i]);
            }
            var rng = new Pcg32Rng(seed, RollStream);
            long value = Math.Max(0, unitValue);
            for (int unit = 0; unit < units; unit++)
            {
                RollWeights(debtsAfter.Slice(0, n), value, halfWeightDebt, weights);
                if (unit == 0)
                    weights.CopyTo(firstWeights);
                int winner = n == 1 ? 0 : Pick(weights, rng);
                won[winner]++;
                debtsAfter[winner] = debtsAfter[winner] > long.MaxValue - value ? long.MaxValue : debtsAfter[winner] + value;
            }
        }
    }
}
