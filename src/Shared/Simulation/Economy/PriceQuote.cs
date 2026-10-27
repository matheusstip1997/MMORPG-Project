using System;

namespace Shared.Simulation.Economy
{
    /// <summary>What one item's price depends on now (R-4.2-02).</summary>
    public readonly struct PriceInputs
    {
        public PriceInputs(int baseGold, int rarityPercent, in DemandCurveShape curve, int floorPercent, int maxPercent,
            long decayedSalesMilli, long expectedSalesMilli)
        {
            BaseGold = baseGold;
            RarityPercent = rarityPercent;
            Curve = curve;
            FloorPercent = floorPercent;
            MaxPercent = maxPercent;
            DecayedSalesMilli = decayedSalesMilli;
            ExpectedSalesMilli = expectedSalesMilli;
        }

        /// <summary><c>PriceBase.baseGold</c>.</summary>
        public int BaseGold { get; }

        /// <summary><c>RarityMult.pricePercent</c> of the stack's rarity.</summary>
        public int RarityPercent { get; }

        /// <summary>The <c>price</c> demand curve.</summary>
        public DemandCurveShape Curve { get; }

        public int FloorPercent { get; }
        public int MaxPercent { get; }

        /// <summary>The item's decayed sales counter before this sale.</summary>
        public long DecayedSalesMilli { get; }

        /// <summary>The expected sales for the active players (<see cref="DemandMath.ExpectedMilli"/>).</summary>
        public long ExpectedSalesMilli { get; }
    }

    /// <summary>A quote: the total and the first and last unit's price.</summary>
    public readonly struct PriceQuoteResult
    {
        public PriceQuoteResult(long total, int firstUnit, int lastUnit)
        {
            Total = total;
            FirstUnit = firstUnit;
            LastUnit = lastUnit;
        }

        public long Total { get; }
        public int FirstUnit { get; }
        public int LastUnit { get; }
    }

    /// <summary>
    /// The outpost sell price (R-4.2-02): base x rarity multiplier x dynamic multiplier, <b>applied per unit
    /// sequentially</b>: unit <c>i</c> of a sale is priced as if the <c>i</c> units before it were already sold, so selling
    /// 100 at once pays exactly what 100 sales of one would. The dynamic multiplier is the <c>price</c> curve at the item's
    /// decayed sales ratio (user decision 2026-10-27: sales only), clamped to the floor and the maximum. Integer math,
    /// rounded down per unit (rule 6).
    /// </summary>
    public static class PriceQuote
    {
        /// <summary>The dynamic multiplier in whole percent after <paramref name="decayedSalesMilli"/> sold.</summary>
        public static int DynamicPercent(in PriceInputs inputs, long decayedSalesMilli) =>
            DemandMath.Clamp(inputs.Curve.Evaluate(DemandMath.RatioPermille(decayedSalesMilli, inputs.ExpectedSalesMilli)),
                inputs.FloorPercent, inputs.MaxPercent);

        /// <summary>Gold for one unit at <paramref name="dynamicPercent"/>.</summary>
        public static int UnitPrice(int baseGold, int rarityPercent, int dynamicPercent) =>
            (int)((long)baseGold * rarityPercent * dynamicPercent / 10_000);

        /// <summary>
        /// The quote for <paramref name="count"/> units. When <paramref name="units"/> is not empty it receives each unit's
        /// price, as many as fit (the per-unit sequence of spec 4.2).
        /// </summary>
        public static PriceQuoteResult Quote(in PriceInputs inputs, int count, Span<int> units = default)
        {
            if (count <= 0)
                throw new ArgumentOutOfRangeException(nameof(count));
            long total = 0;
            int first = 0, last = 0;
            for (int i = 0; i < count; i++)
            {
                int dynamic = DynamicPercent(inputs, inputs.DecayedSalesMilli + i * DemandMath.Unit);
                int price = UnitPrice(inputs.BaseGold, inputs.RarityPercent, dynamic);
                if (i == 0)
                    first = price;
                last = price;
                total += price;
                if (i < units.Length)
                    units[i] = price;
            }
            return new PriceQuoteResult(total, first, last);
        }
    }
}
