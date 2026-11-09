namespace Shared.Data.Tables
{
    /// <summary>
    /// The trade window (spec 4.5 "M4 trade window", M4 decision 5; WP-M4-4). Every number is a placeholder (balance is deferred).
    /// TODO(open) decision 14: the level that unlocks trading (no <c>UnlockSchedule</c> row until WP-M4-11).
    /// </summary>
    [Table("TradeSettings", 1)]
    [Singleton]
    public sealed class TradeSettings : IValidatableRow, ISetValidatableRow
    {
        /// <summary>Most distance between the two players (cm), checked at invite, accept and every tick of an open trade.</summary>
        [Range(1, 100_000)] public int RangeCm { get; set; }

        /// <summary>Item offers per side (slots of the window).</summary>
        [Range(1, 12)] public int MaxSlots { get; set; }

        /// <summary>Most gold one side may put in one trade; not above <c>EconomySettings.maxGold</c>.</summary>
        [Range(0, 9_000_000_000_000_000)] public long MaxGoldPerTrade { get; set; }

        /// <summary>How long an invite waits for an answer before it lapses (TradeTimeout).</summary>
        [Range(1_000, 600_000)] public int InviteTimeoutMs { get; set; }

        /// <summary>How long an accepted trade may stay open before both sides locked (TradeTimeout).</summary>
        [Range(1_000, 3_600_000)] public int LockTimeoutMs { get; set; }

        /// <summary>After both sides locked, how long they have to both confirm (TradeTimeout).</summary>
        [Range(1_000, 600_000)] public int ConfirmTimeoutMs { get; set; }

        /// <summary>After a trade or an invite ends without a swap, neither player may start another for this long (TradeCooldown).</summary>
        [Range(0, 600_000)] public int CancelCooldownMs { get; set; }

        /// <summary>Owner history entries a traded instance keeps (spec 4.1 <c>ownerHistory</c>; the oldest drop first).</summary>
        [Range(1, 64)] public int OwnerHistoryMax { get; set; }

        /// <summary>While both sides are locked, the window's countdown is resent this often.</summary>
        [Range(100, 60_000)] public int StateResendMs { get; set; }

        public void Validate(RowErrors errors)
        {
            if (InviteTimeoutMs > LockTimeoutMs)
                errors.Add(nameof(InviteTimeoutMs), $"{InviteTimeoutMs} is longer than lockTimeoutMs {LockTimeoutMs}");
        }

        public void Validate(ConfigSet set, RowErrors errors)
        {
            if (set.TryGet(typeof(EconomySettings), out var table) && table.Rows.Count == 1
                && table.Rows[0] is EconomySettings economy && MaxGoldPerTrade > economy.MaxGold)
                errors.Add(nameof(MaxGoldPerTrade), $"{MaxGoldPerTrade} is above EconomySettings.maxGold {economy.MaxGold}");
        }

        public static TradeSettings? Find(ConfigSet set) =>
            set.TryGet(typeof(TradeSettings), out var table) && table.Rows.Count == 1 ? (TradeSettings)table.Rows[0] : null;
    }
}
