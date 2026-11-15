namespace Shared.Simulation.Economy
{
    /// <summary>
    /// What two accounts gave each other in one UTC day (spec 4.5 R-4.5-02, M4 decision 17; WP-M4-15): the gold and the items'
    /// value (<see cref="ItemValue"/>) from the lower account id to the higher and back, and how many transfers.
    /// </summary>
    public readonly struct TransferPairDay
    {
        public TransferPairDay(long goldLowToHigh, long goldHighToLow, long itemValueLowToHigh, long itemValueHighToLow, int transfers)
        {
            GoldLowToHigh = goldLowToHigh;
            GoldHighToLow = goldHighToLow;
            ItemValueLowToHigh = itemValueLowToHigh;
            ItemValueHighToLow = itemValueHighToLow;
            Transfers = transfers;
        }

        public long GoldLowToHigh { get; }
        public long GoldHighToLow { get; }
        public long ItemValueLowToHigh { get; }
        public long ItemValueHighToLow { get; }
        public int Transfers { get; }

        /// <summary>This day with one more transfer added.</summary>
        public TransferPairDay Add(long goldLowToHigh, long goldHighToLow, long itemValueLowToHigh, long itemValueHighToLow) =>
            new TransferPairDay(GoldLowToHigh + goldLowToHigh, GoldHighToLow + goldHighToLow, ItemValueLowToHigh + itemValueLowToHigh,
                ItemValueHighToLow + itemValueHighToLow, Transfers + 1);
    }

    /// <summary>
    /// The large-transfer flag rule (spec 4.5 R-4.5-02 "large one-way transfers between accounts are flagged", M4 decision 17;
    /// WP-M4-15; a pure function with unit tests). One-way means <b>net</b> over the pair's UTC day: the gold one account gave
    /// the other minus what it got back, and the value (gold plus the items' value) the same way. A pair crosses the gold
    /// threshold when the absolute net gold reaches it, the value threshold when the absolute net value does; the giver is the
    /// side the net value leaves (the net gold's when the value nets to zero). A flag never changes a transfer.
    /// </summary>
    public static class TransferFlagMath
    {
        /// <summary>The net gold is over its threshold.</summary>
        public const int GoldCrossed = 1;

        /// <summary>The net value is over its threshold.</summary>
        public const int ValueCrossed = 2;

        public const long DayMs = 86_400_000;

        /// <summary>Net gold from the lower account to the higher (negative: the higher gave).</summary>
        public static long NetGold(in TransferPairDay day) => day.GoldLowToHigh - day.GoldHighToLow;

        /// <summary>Net value (gold plus item value) from the lower account to the higher (negative: the higher gave).</summary>
        public static long NetValue(in TransferPairDay day) =>
            day.GoldLowToHigh + day.ItemValueLowToHigh - (day.GoldHighToLow + day.ItemValueHighToLow);

        /// <summary>
        /// The thresholds the day is over: <see cref="GoldCrossed"/>, <see cref="ValueCrossed"/>, both or 0. The gold threshold
        /// counts only <b>one-sided</b> gold (user answer 2026-11-15, PR #258): the absolute net gold reaches
        /// <paramref name="goldThreshold"/> and the items' value that went back to the side that gave the gold is below
        /// <paramref name="oneSidedGoldPercent"/> % of that net gold, so an ordinary purchase (gold for an item worth about its
        /// price) is not flagged on gold. The value threshold is unchanged and still catches an overpayment.
        /// </summary>
        public static int Crossed(in TransferPairDay day, long goldThreshold, long valueThreshold, int oneSidedGoldPercent)
        {
            int crossed = 0;
            if (Abs(NetGold(day)) >= goldThreshold && IsOneSidedGold(day, oneSidedGoldPercent))
                crossed |= GoldCrossed;
            if (Abs(NetValue(day)) >= valueThreshold)
                crossed |= ValueCrossed;
            return crossed;
        }

        /// <summary>
        /// The net gold is one-sided: the items' value that went to the side that gave the net gold is below
        /// <paramref name="percent"/> % of the net gold (rounded down, <see cref="FeeMath.Percent"/>). No net gold is not one-sided.
        /// </summary>
        public static bool IsOneSidedGold(in TransferPairDay day, int percent)
        {
            long net = NetGold(day);
            if (net == 0)
                return false;
            long itemsBack = net > 0 ? day.ItemValueHighToLow : day.ItemValueLowToHigh;
            return itemsBack < FeeMath.Percent(Abs(net), percent);
        }

        /// <summary>True when the lower account is the giver.</summary>
        public static bool LowGives(in TransferPairDay day)
        {
            long value = NetValue(day);
            return value != 0 ? value > 0 : NetGold(day) >= 0;
        }

        /// <summary>The UTC day number (days since 1970-01-01) of a time in Unix milliseconds.</summary>
        public static long UtcDay(long utcMs) => utcMs >= 0 ? utcMs / DayMs : (utcMs - (DayMs - 1)) / DayMs;

        private static long Abs(long value) => value == long.MinValue ? long.MaxValue : value < 0 ? -value : value;
    }
}
