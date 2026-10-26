using System;
using System.Collections.Generic;
using Shared.Util;

namespace Shared.Simulation.Items
{
    /// <summary>One possible result of a loot roll: an item, its rarity tier, a weight and a count range.</summary>
    public readonly struct LootChoice
    {
        public LootChoice(ushort itemNetId, byte rarity, int weight, int countMin, int countMax)
        {
            ItemNetId = itemNetId;
            Rarity = rarity;
            Weight = weight;
            CountMin = countMin;
            CountMax = countMax;
        }

        public ushort ItemNetId { get; }
        public byte Rarity { get; }
        public int Weight { get; }
        public int CountMin { get; }
        public int CountMax { get; }
    }

    /// <summary>
    /// The loot roll (R-4.3-01, spec 10.2 "loot roll" is a pure function): <c>rolls</c> times, pick one choice or nothing by
    /// weight, then the count evenly in its range. Each roll draws exactly twice from the generator when it hits a choice
    /// (weight, count) and once when it hits nothing, so a logged seed reproduces a kill's drops (architecture rule 6).
    /// </summary>
    public static class LootRoll
    {
        /// <summary>Appends one stack per roll that hit a choice to <paramref name="drops"/>.</summary>
        public static void Roll(IReadOnlyList<LootChoice> choices, int rolls, int nothingWeight, IRng rng, List<ItemStack> drops)
        {
            if (nothingWeight < 0)
                throw new ArgumentOutOfRangeException(nameof(nothingWeight));
            long total = nothingWeight;
            for (int i = 0; i < choices.Count; i++)
                total += choices[i].Weight;
            if (total <= 0 || total > int.MaxValue)
                throw new ArgumentException("total weight must be in 1..int.MaxValue", nameof(choices));

            for (int r = 0; r < rolls; r++)
            {
                int pick = rng.NextInt((int)total);
                if (pick < nothingWeight)
                    continue;
                pick -= nothingWeight;
                for (int i = 0; i < choices.Count; i++)
                {
                    var choice = choices[i];
                    if (pick < choice.Weight)
                    {
                        int count = rng.NextInt(choice.CountMin, choice.CountMax + 1);
                        drops.Add(new ItemStack(choice.ItemNetId, choice.Rarity, false, count));
                        break;
                    }
                    pick -= choice.Weight;
                }
            }
        }
    }
}
