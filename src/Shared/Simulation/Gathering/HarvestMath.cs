using System;
using System.Collections.Generic;
using Shared.Data.Tables;
using Shared.Simulation.Jobs;
using Shared.Util;

namespace Shared.Simulation.Gathering
{
    /// <summary>One entry of a node's yield table (<see cref="NodeYieldEntry"/>), as the roll reads it.</summary>
    public readonly struct HarvestChoice
    {
        public HarvestChoice(ushort itemNetId, byte rarity, int weight, int countMin, int countMax, int minJobLevel, bool isGem = false)
        {
            ItemNetId = itemNetId;
            Rarity = rarity;
            Weight = weight;
            CountMin = countMin;
            CountMax = countMax;
            MinJobLevel = minJobLevel;
            IsGem = isGem;
        }

        public ushort ItemNetId { get; }
        public byte Rarity { get; }
        public int Weight { get; }
        public int CountMin { get; }
        public int CountMax { get; }
        public int MinJobLevel { get; }

        /// <summary>A gem find (the item carries the <c>roughGem</c> or <c>gem</c> material tag): the gem-find bonus raises its chance.</summary>
        public bool IsGem { get; }
    }

    /// <summary>What a harvest yields: one stack of an entry's item.</summary>
    public readonly struct HarvestYield
    {
        public HarvestYield(int entryIndex, ushort itemNetId, byte rarity, int count)
        {
            EntryIndex = entryIndex;
            ItemNetId = itemNetId;
            Rarity = rarity;
            Count = count;
        }

        /// <summary>Index of the picked entry in the table; -1 when no entry was in reach of the job level.</summary>
        public int EntryIndex { get; }
        public ushort ItemNetId { get; }
        public byte Rarity { get; }
        /// <summary>Units added, at least 1 for a picked entry.</summary>
        public int Count { get; }
        public bool IsEmpty => EntryIndex < 0;
    }

    /// <summary>
    /// The harvest yield roll (spec 5.2 "yield is a server roll using node tier, job level, skill-tree bonus, and
    /// randomness", R-5.2-02 "may add a yield bonus"; anti-bot "diminishing returns on repetitive harvesting"; WP-M3-5). A
    /// pure, integer function: one entry by weight among those whose <c>minJobLevel</c> the job level reaches (the gem
    /// entries widened by the gem-find bonus), a base count evenly in its range, then that count times <c>(100 + bonus)</c>
    /// percent times the repeat percent, rounded by chance, and never below one unit. The bonus is the job level's, the
    /// tool's and the job tree's together, capped (<see cref="HarvestSettings.MaxBonusPercent"/>). Exactly three draws per
    /// picked entry (entry, count, rounding), so a logged seed reproduces the yield (architecture rule 6).
    /// </summary>
    public static class HarvestMath
    {
        /// <summary>The bonus percent from the job level, the tool and the job tree, capped at <c>maxBonusPercent</c>.</summary>
        public static int BonusPercent(int jobLevel, int toolBonusPercent, int treeBonusPercent, HarvestSettings settings)
        {
            long bonus = (long)settings.YieldPercentPerJobLevel * Math.Max(0, jobLevel - 1)
                + Math.Max(0, toolBonusPercent) + Math.Max(0, treeBonusPercent);
            return (int)Math.Min(bonus, settings.MaxBonusPercent);
        }

        /// <summary>The percent of the count kept after <paramref name="repeats"/> earlier harvests of the same node type.</summary>
        public static int RepeatPercent(int repeats, HarvestSettings settings) =>
            JobMath.RepeatPercent(repeats, settings.YieldRepeatStepPercent, settings.YieldRepeatFloorPercent);

        /// <summary>
        /// <paramref name="baseCount"/> times <paramref name="bonusPercent"/> and <paramref name="repeatPercent"/>: the whole
        /// units, plus one more with the chance of the remainder (one draw, always made), and at least 1.
        /// </summary>
        public static int ScaledCount(int baseCount, int bonusPercent, int repeatPercent, IRng rng)
        {
            const int scale = 10_000;
            long scaled = (long)baseCount * (100 + Math.Max(0, bonusPercent)) * Math.Max(0, repeatPercent);
            long whole = scaled / scale;
            int remainder = (int)(scaled % scale);
            if (rng.NextInt(scale) < remainder)
                whole++;
            return (int)Math.Max(1, Math.Min(whole, ushort.MaxValue));
        }

        /// <summary>Largest total weight of one yield table (the roll draws in hundredths of the total, within an int).</summary>
        public const int MaxTableWeight = 20_000_000;

        /// <summary>
        /// Rolls one yield from <paramref name="table"/> for a harvester of <paramref name="jobLevel"/>. The entry draw is
        /// made in hundredths of the reachable weight: the non-gem entries first, then the gem entries, each group in table
        /// order and weighed within itself; <paramref name="gemFindPoints"/> (the job tree's gem find, decision 13) widens
        /// the gem group by that many percentage points of the whole (never past all of it), so 0 keeps the table's own
        /// odds and a table without gem entries ignores it. Returns an empty yield (no draw) when no entry is in reach of the
        /// job level; the validator keeps a level-1 entry in every table.
        /// </summary>
        public static HarvestYield Roll(IReadOnlyList<HarvestChoice> table, int jobLevel, int bonusPercent, int repeatPercent,
            IRng rng, int gemFindPoints = 0)
        {
            long gems = 0, others = 0;
            for (int i = 0; i < table.Count; i++)
            {
                if (table[i].MinJobLevel > jobLevel)
                    continue;
                if (table[i].IsGem)
                    gems += table[i].Weight;
                else
                    others += table[i].Weight;
            }
            long total = gems + others;
            if (total <= 0)
                return new HarvestYield(-1, 0, 0, 0);
            if (total > MaxTableWeight)
                throw new ArgumentException($"total weight must be in 1..{MaxTableWeight}", nameof(table));

            long scale = total * 100;
            long gemSpan = gems == 0 ? 0
                : others == 0 ? scale
                : Math.Min(scale, gems * 100 + (long)Math.Max(0, gemFindPoints) * total);
            long otherSpan = scale - gemSpan;
            int pick = rng.NextInt((int)scale);
            bool gem = pick >= otherSpan;
            long target = gem ? (pick - otherSpan) * gems / gemSpan : pick * others / otherSpan;
            for (int i = 0; i < table.Count; i++)
            {
                var choice = table[i];
                if (choice.MinJobLevel > jobLevel || choice.IsGem != gem)
                    continue;
                if (target < choice.Weight)
                {
                    int baseCount = rng.NextInt(choice.CountMin, choice.CountMax + 1);
                    int count = ScaledCount(baseCount, bonusPercent, repeatPercent, rng);
                    return new HarvestYield(i, choice.ItemNetId, choice.Rarity, count);
                }
                target -= choice.Weight;
            }
            throw new InvalidOperationException("unreachable: the pick is below the group's weight");
        }

        /// <summary>
        /// The most units a roll from <paramref name="table"/> can yield for this harvester (the bag must have room for the
        /// item of any reachable entry before the channel starts, and for the rolled count at its end).
        /// </summary>
        public static int MaxCount(in HarvestChoice choice, int bonusPercent, int repeatPercent)
        {
            long scaled = (long)choice.CountMax * (100 + Math.Max(0, bonusPercent)) * Math.Max(0, repeatPercent);
            return (int)Math.Max(1, Math.Min((scaled + 9_999) / 10_000, ushort.MaxValue));
        }
    }
}
