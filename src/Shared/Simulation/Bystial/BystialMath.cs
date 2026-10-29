using System;
using System.Collections.Generic;
using Shared.Data.Tables;

namespace Shared.Simulation.Bystial
{
    /// <summary>
    /// One card tier's or family set bonus's bonus as the simulation reads it (spec 14.2, R-14.2-05): a stat bonus
    /// (<see cref="StatId"/> a <c>StatRules</c> index, -1 for none) and one effect. An element or build-up resistance names
    /// the stat it lands on (<see cref="EffectStatId"/>: the element's resistance stat, or <c>buildUpResistPercent</c>);
    /// damage and exp against a family land on no stat (-1).
    /// </summary>
    public readonly struct BystialBonus
    {
        public BystialBonus(int statId, int flat, int percent, BystialEffectKind kind, int effectStatId, int value)
        {
            StatId = statId;
            Flat = flat;
            Percent = percent;
            Kind = kind;
            EffectStatId = effectStatId;
            Value = value;
        }

        public int StatId { get; }
        public int Flat { get; }
        public int Percent { get; }
        public BystialEffectKind Kind { get; }
        public int EffectStatId { get; }
        public int Value { get; }
    }

    /// <summary>A card in a book slot: its monster's family and its current tier's bonus.</summary>
    public readonly struct PlacedCard
    {
        public PlacedCard(MonsterFamily family, BystialBonus bonus)
        {
            Family = family;
            Bonus = bonus;
        }

        public MonsterFamily Family { get; }
        public BystialBonus Bonus { get; }
    }

    /// <summary>A family set bonus (<see cref="BystialFamilyBonus"/>): applies while at least <see cref="CardsPlaced"/> cards of <see cref="Family"/> are placed.</summary>
    public readonly struct FamilySetBonus
    {
        public FamilySetBonus(MonsterFamily family, int cardsPlaced, BystialBonus bonus)
        {
            Family = family;
            CardsPlaced = cardsPlaced;
            Bonus = bonus;
        }

        public MonsterFamily Family { get; }
        public int CardsPlaced { get; }
        public BystialBonus Bonus { get; }
    }

    /// <summary>The caps of rule 2 (<see cref="BystialStatCap"/> by stat index, <see cref="BystialEffectCap"/> by kind); a missing cap is 0.</summary>
    public sealed class BystialCaps
    {
        public BystialCaps(int[] maxFlat, int[] maxPercent, int[] maxEffect)
        {
            if (maxFlat.Length != maxPercent.Length)
                throw new ArgumentException("one flat and one percent cap per stat", nameof(maxPercent));
            if (maxEffect.Length != BystialEffectKinds.ArrayLength)
                throw new ArgumentException("one cap per effect kind", nameof(maxEffect));
            MaxFlat = maxFlat;
            MaxPercent = maxPercent;
            MaxEffect = maxEffect;
        }

        /// <summary>By stat index: the most a stat's bonuses add up to, flat, either sign.</summary>
        public int[] MaxFlat { get; }

        /// <summary>By stat index: the same in whole percents.</summary>
        public int[] MaxPercent { get; }

        /// <summary>By <see cref="BystialEffectKind"/>: the most one element, one family or the build-up resistance adds up to.</summary>
        public int[] MaxEffect { get; }

        public int StatCount => MaxFlat.Length;
    }

    /// <summary>One stat modifier of the book (source type <c>bystialCard</c>).</summary>
    public readonly struct BookStatBonus
    {
        public BookStatBonus(int statId, int flat, int percent)
        {
            StatId = statId;
            Flat = flat;
            Percent = percent;
        }

        public int StatId { get; }
        public int Flat { get; }
        public int Percent { get; }
    }

    /// <summary>
    /// What a book gives, capped (R-14.2-05): one stat modifier per stat in stat order, and the whole percents of damage and
    /// exp against each family (indexed by <see cref="MonsterFamily"/> value). Immutable once built.
    /// </summary>
    public sealed class BookBonuses
    {
        public static readonly BookBonuses Empty = new BookBonuses(Array.Empty<BookStatBonus>(),
            new int[MonsterFamilies.ArrayLength], new int[MonsterFamilies.ArrayLength]);

        public BookBonuses(BookStatBonus[] stats, int[] damagePercent, int[] expPercent)
        {
            Stats = stats;
            _damagePercent = damagePercent;
            _expPercent = expPercent;
        }

        private readonly int[] _damagePercent;
        private readonly int[] _expPercent;

        public IReadOnlyList<BookStatBonus> Stats { get; }

        /// <summary>Whole percents more damage dealt to monsters of <paramref name="family"/>; 0 for none.</summary>
        public int DamagePercent(MonsterFamily family) => (uint)family < (uint)_damagePercent.Length ? _damagePercent[(int)family] : 0;

        /// <summary>Whole percents more exp from kills of monsters of <paramref name="family"/>; 0 for none.</summary>
        public int ExpPercent(MonsterFamily family) => (uint)family < (uint)_expPercent.Length ? _expPercent[(int)family] : 0;
    }

    /// <summary>
    /// The Bystial book's pure rules (spec 14.2, WP-M2-8): which tier a kill count reaches (R-14.2-02), the next threshold,
    /// and what the placed cards and family set bonuses add up to, capped (R-14.2-05, architecture rule 2). Integer math,
    /// no state (rule 6).
    /// </summary>
    public static class BystialMath
    {
        /// <summary>
        /// The highest tier whose threshold <paramref name="kills"/> reaches; <see cref="BystialTier.None"/> below the first.
        /// <paramref name="thresholds"/> holds the kills of normal, rare, ... in order, strictly rising (the table's rule).
        /// </summary>
        public static BystialTier TierFor(long kills, IReadOnlyList<int> thresholds)
        {
            int tier = 0;
            while (tier < thresholds.Count && tier < BystialTiers.Count && kills >= thresholds[tier])
                tier++;
            return (BystialTier)tier;
        }

        /// <summary>The kills the tier after <paramref name="tier"/> needs; 0 when <paramref name="tier"/> is the ladder's top.</summary>
        public static long NextTierKills(BystialTier tier, IReadOnlyList<int> thresholds) =>
            (int)tier < thresholds.Count ? thresholds[(int)tier] : 0;

        /// <summary><paramref name="value"/> x (100 + <paramref name="percent"/>) / 100, rounded down, never below 0 (the exp bonus).</summary>
        public static long ApplyPercent(long value, int percent)
        {
            if (value <= 0)
                return 0;
            long factor = Math.Max(0, 100L + percent);
            return value > long.MaxValue / Math.Max(1, factor) ? long.MaxValue : value * factor / 100;
        }

        /// <summary>
        /// The book's bonuses: every placed card's bonus, plus every family set bonus whose family has at least its
        /// <c>cardsPlaced</c> cards placed (thresholds add up), summed and then capped: a stat's flat and percent sums to
        /// +-its stat cap, each element's resistance, the build-up resistance and each family's damage and exp to 0..the
        /// effect kind's cap. A stat bonus and an effect on the same stat are capped apart and then added.
        /// </summary>
        public static BookBonuses Aggregate(IReadOnlyList<PlacedCard> placed, IReadOnlyList<FamilySetBonus> setBonuses, BystialCaps caps)
        {
            if (placed.Count == 0)
                return BookBonuses.Empty;
            int statCount = caps.StatCount;
            var flat = new long[statCount];
            var percent = new long[statCount];
            var effect = new long[statCount];
            var effectKind = new BystialEffectKind[statCount];
            var damage = new long[MonsterFamilies.ArrayLength];
            var exp = new long[MonsterFamilies.ArrayLength];
            var cards = new int[MonsterFamilies.ArrayLength];

            void Add(in BystialBonus bonus, MonsterFamily family)
            {
                if ((uint)bonus.StatId < (uint)statCount)
                {
                    flat[bonus.StatId] += bonus.Flat;
                    percent[bonus.StatId] += bonus.Percent;
                }
                switch (bonus.Kind)
                {
                    case BystialEffectKind.ElementResistance:
                    case BystialEffectKind.BuildUpResistance:
                        if ((uint)bonus.EffectStatId < (uint)statCount)
                        {
                            effect[bonus.EffectStatId] += bonus.Value;
                            effectKind[bonus.EffectStatId] = bonus.Kind;
                        }
                        break;
                    case BystialEffectKind.FamilyDamage:
                        damage[(int)family] += bonus.Value;
                        break;
                    case BystialEffectKind.FamilyExp:
                        exp[(int)family] += bonus.Value;
                        break;
                }
            }

            for (int i = 0; i < placed.Count; i++)
            {
                var card = placed[i];
                if ((uint)card.Family < (uint)cards.Length)
                    cards[(int)card.Family]++;
                Add(card.Bonus, card.Family);
            }
            for (int i = 0; i < setBonuses.Count; i++)
            {
                var set = setBonuses[i];
                if ((uint)set.Family < (uint)cards.Length && cards[(int)set.Family] >= set.CardsPlaced)
                    Add(set.Bonus, set.Family);
            }

            var stats = new List<BookStatBonus>();
            for (int stat = 0; stat < statCount; stat++)
            {
                long statFlat = Clamp(flat[stat], -caps.MaxFlat[stat], caps.MaxFlat[stat]);
                long statPercent = Clamp(percent[stat], -caps.MaxPercent[stat], caps.MaxPercent[stat]);
                if (effect[stat] != 0)
                    statFlat += Clamp(effect[stat], 0, caps.MaxEffect[(int)effectKind[stat]]);
                if (statFlat != 0 || statPercent != 0)
                    stats.Add(new BookStatBonus(stat, (int)statFlat, (int)statPercent));
            }
            int damageCap = caps.MaxEffect[(int)BystialEffectKind.FamilyDamage];
            int expCap = caps.MaxEffect[(int)BystialEffectKind.FamilyExp];
            var damagePercent = new int[MonsterFamilies.ArrayLength];
            var expPercent = new int[MonsterFamilies.ArrayLength];
            for (int family = 1; family < MonsterFamilies.ArrayLength; family++)
            {
                damagePercent[family] = (int)Clamp(damage[family], 0, damageCap);
                expPercent[family] = (int)Clamp(exp[family], 0, expCap);
            }
            return new BookBonuses(stats.ToArray(), damagePercent, expPercent);
        }

        private static long Clamp(long value, long min, long max) => value < min ? min : value > max ? max : value;
    }
}
