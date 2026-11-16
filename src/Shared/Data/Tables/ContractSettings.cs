using Shared.Protocol;

namespace Shared.Data.Tables
{
    /// <summary>
    /// Contracts and the portfolio (spec 5.4 R-5.4-01..04, M4 decisions 10 and 11, user answers 2026-11-14 and 2026-11-15;
    /// WP-M4-10). A customer proposes a forge job (a mold craft, a mold-making, a refine or a conversion) with the materials it
    /// supplies and a gold payment, both held in escrow at the proposal; a crafter accepts at the contract board; the deadline the
    /// customer chose runs from the acceptance in real time. The decision's numbers are the deadline bounds (1 to 72 hours), the
    /// crafter's cancel cost after work started (10 % of the gold, a sink), the minimum value (10 gold) and the per-pair cap (5 a
    /// UTC day, R-5.4-04 anti-farming). Every other number is a placeholder (balance is deferred).
    /// </summary>
    [Table("ContractSettings", 1)]
    [Singleton]
    public sealed class ContractSettings : IValidatableRow, ISetValidatableRow
    {
        /// <summary>The shortest deadline a customer may choose, in hours from the acceptance (<c>ContractDeadlineInvalid</c>).</summary>
        [Range(1, 255)] public int MinDeadlineHours { get; set; }

        /// <summary>The longest deadline a customer may choose, in hours (one byte on the wire).</summary>
        [Range(1, 255)] public int MaxDeadlineHours { get; set; }

        /// <summary>How long an unaccepted proposal stays on the board before it expires and returns by mail (user answer 2026-11-15).</summary>
        [Range(1, 720)] public int ProposalOpenHours { get; set; }

        /// <summary>The crafter's cancel after work started costs this percent of the contract gold, paid from the crafter's own gold (a sink).</summary>
        [Range(0, 100)] public int CancelCostPercent { get; set; }

        /// <summary>The lowest contract gold (<c>ContractValueTooLow</c>; R-5.4-04 anti-farming).</summary>
        [Range(1, 9_000_000_000_000_000)] public long MinGold { get; set; }

        /// <summary>Contracts one pair of characters (either way round) may accept per UTC day (<c>ContractPairCap</c>; R-5.4-04).</summary>
        [Range(1, 1_000)] public int PairCapPerDay { get; set; }

        /// <summary>Open contracts (proposed or accepted) one customer may have (<c>ContractTooMany</c>).</summary>
        [Range(1, ReliableMessages.MaxMyContracts)] public int MaxOpenPerCustomer { get; set; }

        /// <summary>Accepted contracts one crafter may hold at once (<c>ContractTooMany</c>).</summary>
        [Range(1, ReliableMessages.MaxMyContracts)] public int MaxActivePerCrafter { get; set; }

        /// <summary>Rows of one board page, oldest first.</summary>
        [Range(1, ReliableMessages.MaxContractBoardRows)] public int BoardPageSize { get; set; }

        /// <summary>Token bucket of the contract and portfolio requests per character (spec 7.6).</summary>
        [Range(1, 1_000)] public int RequestsPerPeriod { get; set; }

        [Range(100, 3_600_000)] public int RequestPeriodMs { get; set; }

        [Range(1, 1_000)] public int RequestBurst { get; set; }

        /// <summary>A session's contract requests waiting for the next tick (bounds a flooding client).</summary>
        [Range(1, 256)] public int MaxQueuedRequests { get; set; }

        /// <summary>Contracts the expiry job starts closing per tick at most (the rest wait for the next tick).</summary>
        [Range(1, 10_000)] public int ExpiryBatch { get; set; }

        /// <summary>Pinned snapshots of a portfolio's showcase (M4 decision 11: up to 6).</summary>
        [Range(1, ReliableMessages.MaxShowcase)] public int ShowcaseMax { get; set; }

        public void Validate(RowErrors errors)
        {
            if (MinDeadlineHours > MaxDeadlineHours)
                errors.Add(nameof(MinDeadlineHours), "must not be above MaxDeadlineHours");
            if (RequestBurst < RequestsPerPeriod)
                errors.Add(nameof(RequestBurst), "must be at least RequestsPerPeriod");
        }

        public void Validate(ConfigSet set, RowErrors errors)
        {
            if (set.TryGet(typeof(EconomySettings), out var table) && table.Rows.Count == 1
                && table.Rows[0] is EconomySettings economy && MinGold > economy.MaxGold)
                errors.Add(nameof(MinGold), $"{MinGold} is above EconomySettings.maxGold {economy.MaxGold}");
        }

        public static ContractSettings? Find(ConfigSet set) =>
            set.TryGet(typeof(ContractSettings), out var table) && table.Rows.Count == 1 ? (ContractSettings)table.Rows[0] : null;
    }
}
