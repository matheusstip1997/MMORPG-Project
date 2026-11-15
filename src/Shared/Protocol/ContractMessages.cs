using System;
using MemoryPack;

namespace Shared.Protocol
{
    /// <summary>What a <see cref="ContractRequest"/> asks and a <see cref="ContractResult"/> answers (spec 5.4; WP-M4-10). Append only.</summary>
    public enum ContractAction : byte
    {
        None = 0,
        /// <summary>The customer proposes a contract (anywhere, through the C window): its gold and materials go into escrow.</summary>
        Propose = 1,
        /// <summary>A crafter accepts an open proposal at the contract board; the deadline starts.</summary>
        Accept = 2,
        /// <summary>
        /// The customer cancels before work started (free), or the crafter cancels an accepted contract (after work started it
        /// costs <c>ContractSettings.cancelCostPercent</c> of the gold, paid by the crafter). Everything returns to the customer by mail.
        /// </summary>
        Cancel = 3,
        /// <summary>The player's own contracts (answered by a <see cref="ContractList"/>).</summary>
        Mine = 4,
    }

    /// <summary>
    /// A contract's state (spec 5.4: <c>Proposed → Accepted → InProgress → Completed|Expired|Cancelled|Failed</c>). Append only.
    /// <see cref="InProgress"/> is shown while a forge session of the contract runs (memory only: a lost session leaves it accepted).
    /// </summary>
    public enum ContractStatus : byte
    {
        None = 0,
        Proposed = 1,
        Accepted = 2,
        InProgress = 3,
        Completed = 4,
        Expired = 5,
        Cancelled = 6,
        /// <summary>A backstop: the contract's escrow was not found at load; an admin closes it (TODO(open)).</summary>
        Failed = 7,
    }

    /// <summary>Who supplies one input slot of a contract (R-5.4-01 "either side may supply materials"). Append only.</summary>
    public enum ContractSupplier : byte
    {
        /// <summary>The customer: the units are held in escrow from the proposal and never pass through the crafter's bag (R-5.4-02).</summary>
        Customer = 0,
        /// <summary>The crafter: the units come from the crafter's Normal bag at the forge, reserved there as for any forge session.</summary>
        Crafter = 1,
    }

    /// <summary>How the viewer stands to a contract it sees.</summary>
    public enum ContractRole : byte
    {
        None = 0,
        Customer = 1,
        Crafter = 2,
    }

    /// <summary>
    /// Why the server refused a contract or portfolio request (spec 5.4, 9.7 reason codes; WP-M4-10). Append only;
    /// <see cref="ReliableMessages.LastContractReason"/> is the highest. Names shared with other enums share their
    /// <c>ReasonCodeStrings</c> row (<c>NotEnoughGold</c>, <c>MailboxFull</c>, <c>OutOfRange</c>, <c>InCombat</c>, ...).
    /// </summary>
    public enum ContractReason : byte
    {
        None = 0,
        /// <summary>No open contract with that id (completed, expired, cancelled, or never there).</summary>
        ContractNotFound = 1,
        /// <summary>The contract is not in a state that allows this (accepted already, work started, not the crafter's, ...).</summary>
        ContractStateInvalid = 2,
        /// <summary>The gold is below <c>ContractSettings.minGold</c> (R-5.4-04).</summary>
        ContractValueTooLow = 3,
        /// <summary>This pair accepted <c>ContractSettings.pairCapPerDay</c> contracts this UTC day (R-5.4-04).</summary>
        ContractPairCap = 4,
        /// <summary>A stolen, tainted, non-tradable, sell-only, quest or equipped item cannot be put in a contract (R-5.4-03, R-4.1-04).</summary>
        ContractNotTradable = 5,
        /// <summary>The crafter does not hold the job or its level for the contract's definition.</summary>
        ContractJobRequired = 6,
        /// <summary>The deadline is outside <c>ContractSettings.minDeadlineHours..maxDeadlineHours</c>.</summary>
        ContractDeadlineInvalid = 7,
        /// <summary>A customer cannot accept its own contract.</summary>
        ContractOwn = 8,
        /// <summary>Too many open contracts as customer, or accepted ones as crafter (<c>ContractSettings</c>).</summary>
        ContractTooMany = 9,
        /// <summary>The definition is unknown or not craftable, or the slots do not match it (count, supplier, item, units).</summary>
        ContractTermsInvalid = 10,
        /// <summary>The gold does not cover the payment, or the crafter's cancel cost.</summary>
        NotEnoughGold = 11,
        /// <summary>The customer's mailbox is full: a contract is a new mail source (spec 4.5).</summary>
        MailboxFull = 12,
        /// <summary>More requests than <c>ContractSettings</c> allows per period (spec 7.6 rate limits).</summary>
        ContractRateLimited = 13,
        /// <summary>The contract board is not in reach.</summary>
        OutOfRange = 14,
        /// <summary>No contract board with that entity id is visible.</summary>
        NpcNotFound = 15,
        /// <summary>That NPC is not a contract board, or this world has no contracts.</summary>
        InteractionUnavailable = 16,
        /// <summary>The character's inventory or mailbox is not loaded.</summary>
        InventoryUnavailable = 17,
        /// <summary>A contract never interrupts combat or an action under way.</summary>
        InCombat = 18,
        /// <summary>Another operation of this contract is saving (a forge finish, a cancel, an expiry); try again in a moment.</summary>
        ContractBusy = 19,
        /// <summary>A bag slot is not what the proposal names (another item, fewer units, reserved).</summary>
        ContractChanged = 20,
        /// <summary>The contract passed its deadline; everything is on the way back to the customer.</summary>
        ContractExpired = 21,
        /// <summary>That player hides its portfolio (R-5.4-04 hide flag).</summary>
        PortfolioHidden = 22,
        /// <summary>No such player, or its portfolio could not be read; try again.</summary>
        PortfolioNotFound = 23,
        /// <summary>The showcase holds <c>ContractSettings.showcaseMax</c> snapshots already.</summary>
        ShowcaseFull = 24,
        /// <summary>Only an item the player crafted can be pinned to its showcase.</summary>
        PortfolioNotCrafter = 25,
        /// <summary>The crafter would pass the gold cap with the payment.</summary>
        GoldCapReached = 26,
    }

    /// <summary>What a <see cref="PortfolioRequest"/> asks (R-5.4-04, M4 decision 11). Append only.</summary>
    public enum PortfolioAction : byte
    {
        None = 0,
        /// <summary>Read a portfolio: the player's own, a contract's crafter's, or a player in view.</summary>
        View = 1,
        /// <summary>Pin a snapshot of an item the player crafted to its showcase (the snapshot survives the item's loss).</summary>
        Pin = 2,
        /// <summary>Unpin a showcase slot.</summary>
        Unpin = 3,
        /// <summary>Hide the portfolio from others, or show it again (<see cref="PortfolioRequest.Hidden"/>).</summary>
        SetHidden = 4,
    }

    /// <summary>What a portfolio counter counts (R-5.4-04: crafts, enchant results, contracts completed, expired, cancelled). Append only.</summary>
    public enum PortfolioOutcome : byte
    {
        None = 0,
        Crafted = 1,
        Refined = 2,
        Converted = 3,
        ConvertFailed = 4,
        EnchantUp = 5,
        EnchantSame = 6,
        EnchantDown = 7,
        ContractCompleted = 8,
        ContractExpired = 9,
        ContractCancelled = 10,
    }

    /// <summary>
    /// One input slot of a contract's terms (R-5.4-01). For the customer's slots the proposal names the bag place
    /// (<see cref="Container"/>, <see cref="Slot"/>) whose units go into escrow; a view shows the held item. For the crafter's slots
    /// <see cref="ItemNetId"/> is the item the terms require, 0 for any item the slot takes.
    /// </summary>
    [MemoryPackable]
    public sealed partial class ContractSlot
    {
        public ContractSupplier Supplier { get; set; }

        public byte Container { get; set; }

        public byte Slot { get; set; }

        public ushort ItemNetId { get; set; }

        /// <summary><c>RarityDef.tier</c> of a held item (views only).</summary>
        public byte Rarity { get; set; }

        /// <summary>The units of the slot (the definition's input count).</summary>
        public ushort Count { get; set; }
    }

    /// <summary>
    /// One contract as a client sees it. No names (rule 8: a name reaches clients only through <c>DisplayIdentity</c>; as the
    /// auction, TODO(open) a rule for showing customers and crafters). <see cref="Role"/> is the viewer's own part, if any.
    /// </summary>
    [MemoryPackable]
    public sealed partial class ContractEntry
    {
        public long ContractId { get; set; }

        public ContractStatus Status { get; set; }

        public ContractRole Role { get; set; }

        public ForgeKind Kind { get; set; }

        public ushort DefNetId { get; set; }

        public long Gold { get; set; }

        public byte DeadlineHours { get; set; }

        /// <summary>A proposal's end on the board (UTC ms), 0 once accepted.</summary>
        public long OpenUntilUtcMs { get; set; }

        /// <summary>An accepted contract's deadline (UTC ms), 0 before the acceptance.</summary>
        public long DeadlineUtcMs { get; set; }

        /// <summary>The crafter started a forge session on it (the customer can no longer cancel; the crafter's cancel costs).</summary>
        public bool WorkStarted { get; set; }

        public ContractSlot[] Slots { get; set; } = Array.Empty<ContractSlot>();
    }

    /// <summary>
    /// Client to server, reliable (WP-M4-10, protocol 51): one page of open proposals at the contract board
    /// <see cref="NpcEntityId"/>, oldest first, after <see cref="AfterContractId"/> (0 for the first page). Filters are optional
    /// (0: any). Answered by a <see cref="ContractBoard"/> with the same <see cref="RequestId"/>.
    /// </summary>
    [MemoryPackable]
    public sealed partial class ContractBoardRequest
    {
        public uint RequestId { get; set; }

        public uint NpcEntityId { get; set; }

        /// <summary>The definition's job (<c>JobDef.netId</c>); 0 for any job.</summary>
        public byte JobNetId { get; set; }

        /// <summary>Molds or recipes only; <see cref="ForgeKind.None"/> for both.</summary>
        public ForgeKind Kind { get; set; }

        public long AfterContractId { get; set; }
    }

    /// <summary>Server to client, reliable (WP-M4-10, protocol 51): one board page; <see cref="HasMore"/> when a next page exists.</summary>
    [MemoryPackable]
    public sealed partial class ContractBoard
    {
        public uint RequestId { get; set; }

        public ContractReason Reason { get; set; }

        public ContractEntry[] Rows { get; set; } = Array.Empty<ContractEntry>();

        public bool HasMore { get; set; }
    }

    /// <summary>
    /// Client to server, reliable (WP-M4-10, protocol 51). <see cref="ContractAction.Propose"/> carries the terms
    /// (<see cref="Kind"/>, <see cref="DefNetId"/>, <see cref="Gold"/>, <see cref="DeadlineHours"/>, one <see cref="Slots"/> row per
    /// input of the definition in order); <see cref="ContractAction.Accept"/> names the board (<see cref="NpcEntityId"/>) and
    /// <see cref="ContractId"/>; <see cref="ContractAction.Cancel"/> names <see cref="ContractId"/>; <see cref="ContractAction.Mine"/>
    /// nothing. Answered by a <see cref="ContractResult"/> once the operation committed (rule 5), or a <see cref="ContractList"/>.
    /// </summary>
    [MemoryPackable]
    public sealed partial class ContractRequest
    {
        public uint RequestId { get; set; }

        public uint NpcEntityId { get; set; }

        public ContractAction Action { get; set; }

        public long ContractId { get; set; }

        public ForgeKind Kind { get; set; }

        public ushort DefNetId { get; set; }

        public long Gold { get; set; }

        public byte DeadlineHours { get; set; }

        public ContractSlot[] Slots { get; set; } = Array.Empty<ContractSlot>();
    }

    /// <summary>
    /// Server to client, reliable (WP-M4-10, protocol 51): the answer to a propose, accept or cancel; a refusal at once, an applied
    /// request once its save or release committed (rule 5). <see cref="Gold"/> is the contract gold, <see cref="Fee"/> the
    /// contract fee destroyed at a completion, <see cref="Cost"/> the crafter's cancel cost. Append fields only (spec 11.2).
    /// </summary>
    [MemoryPackable]
    public sealed partial class ContractResult
    {
        public uint RequestId { get; set; }

        public ContractAction Action { get; set; }

        public ContractReason Reason { get; set; }

        public long ContractId { get; set; }

        public long Gold { get; set; }

        public long Fee { get; set; }

        public long Cost { get; set; }
    }

    /// <summary>
    /// Server to client, reliable (WP-M4-10, protocol 51): the player's open contracts as customer and as crafter
    /// (<see cref="RequestId"/> 0 when pushed: at enter world and whenever one of them changes); <see cref="Badge"/> is the C
    /// button's count (open contracts on either side).
    /// </summary>
    [MemoryPackable]
    public sealed partial class ContractList
    {
        public uint RequestId { get; set; }

        public ContractReason Reason { get; set; }

        public ContractEntry[] Rows { get; set; } = Array.Empty<ContractEntry>();

        public ushort Badge { get; set; }
    }

    /// <summary>One portfolio counter (R-5.4-04): per job, item category, rarity and outcome.</summary>
    [MemoryPackable]
    public sealed partial class PortfolioCounter
    {
        /// <summary><c>JobDef.netId</c>, 0 for none.</summary>
        public byte JobNetId { get; set; }

        /// <summary>The output's <c>ItemCategory</c> + 1 (0 when there is no output, a failed conversion).</summary>
        public byte Category { get; set; }

        /// <summary><c>RarityDef.tier</c> of the output, 0 when it has none.</summary>
        public byte Rarity { get; set; }

        public PortfolioOutcome Outcome { get; set; }

        public uint Count { get; set; }
    }

    /// <summary>One pinned showcase snapshot (read-only; it survives the item's loss, R-5.4-04).</summary>
    [MemoryPackable]
    public sealed partial class PortfolioShowcaseEntry
    {
        /// <summary>1 to <c>ContractSettings.showcaseMax</c>.</summary>
        public byte ShowcaseSlot { get; set; }

        public long PinnedUtcMs { get; set; }

        public InventorySlotEntry Item { get; set; } = new InventorySlotEntry();
    }

    /// <summary>
    /// Client to server, reliable (WP-M4-10, protocol 51). <see cref="PortfolioAction.View"/>: the player's own portfolio, or
    /// the crafter of <see cref="ContractId"/> (one of the player's contracts), or the player <see cref="TargetEntityId"/> in
    /// view; <see cref="PortfolioAction.Pin"/>: the instance (<see cref="Container"/>, <see cref="Slot"/>, <see cref="InstanceId"/>);
    /// <see cref="PortfolioAction.Unpin"/>: <see cref="ShowcaseSlot"/>; <see cref="PortfolioAction.SetHidden"/>:
    /// <see cref="Hidden"/>. Answered by a <see cref="PortfolioState"/>.
    /// </summary>
    [MemoryPackable]
    public sealed partial class PortfolioRequest
    {
        public uint RequestId { get; set; }

        public PortfolioAction Action { get; set; }

        public long ContractId { get; set; }

        public uint TargetEntityId { get; set; }

        public byte Container { get; set; }

        public byte Slot { get; set; }

        public long InstanceId { get; set; }

        public byte ShowcaseSlot { get; set; }

        public bool Hidden { get; set; }
    }

    /// <summary>Server to client, reliable (WP-M4-10, protocol 51): a portfolio; <see cref="Own"/> when it is the viewer's own.</summary>
    [MemoryPackable]
    public sealed partial class PortfolioState
    {
        public uint RequestId { get; set; }

        public ContractReason Reason { get; set; }

        public bool Own { get; set; }

        public bool Hidden { get; set; }

        public PortfolioCounter[] Counters { get; set; } = Array.Empty<PortfolioCounter>();

        public PortfolioShowcaseEntry[] Showcase { get; set; } = Array.Empty<PortfolioShowcaseEntry>();
    }

    public static partial class ReliableMessages
    {
        public const ContractAction LastContractAction = ContractAction.Mine;
        public const ContractStatus LastContractStatus = ContractStatus.Failed;
        public const ContractSupplier LastContractSupplier = ContractSupplier.Crafter;
        public const ContractRole LastContractRole = ContractRole.Crafter;
        public const ContractReason LastContractReason = ContractReason.GoldCapReached;
        public const PortfolioAction LastPortfolioAction = PortfolioAction.SetHidden;
        public const PortfolioOutcome LastPortfolioOutcome = PortfolioOutcome.ContractCancelled;

        /// <summary>Rows of one board page at most (<c>ContractSettings.boardPageSize</c> is not larger).</summary>
        public const int MaxContractBoardRows = 50;

        /// <summary>A player's open contracts at most, both roles (<c>ContractSettings</c> caps are not larger).</summary>
        public const int MaxMyContracts = 32;

        /// <summary>Showcase snapshots at most (<c>ContractSettings.showcaseMax</c> is not larger; M4 decision 11: 6).</summary>
        public const int MaxShowcase = 6;

        /// <summary>Portfolio counters one state carries at most (job x category x rarity x outcome rows that are not 0).</summary>
        public const int MaxPortfolioCounters = 256;

        public static bool TryRead(ReadOnlySpan<byte> message, out ContractBoardRequest request)
        {
            request = null!;
            if (!TryBody(message, ReliableMessageId.ContractBoardRequest, out ContractBoardRequest? value) || value == null
                || value.Kind > LastForgeKind || value.AfterContractId < 0)
                return false;
            request = value;
            return true;
        }

        public static bool TryRead(ReadOnlySpan<byte> message, out ContractBoard board)
        {
            board = null!;
            if (!TryBody(message, ReliableMessageId.ContractBoard, out ContractBoard? value) || value == null
                || value.Reason > LastContractReason || !ValidContractRows(value.Rows, MaxContractBoardRows))
                return false;
            board = value;
            return true;
        }

        public static bool TryRead(ReadOnlySpan<byte> message, out ContractRequest request)
        {
            request = null!;
            if (!TryBody(message, ReliableMessageId.ContractRequest, out ContractRequest? value) || value == null
                || value.Action == ContractAction.None || value.Action > LastContractAction || value.ContractId < 0
                || value.Kind > LastForgeKind || value.Gold < 0 || !ValidContractSlots(value.Slots))
                return false;
            request = value;
            return true;
        }

        public static bool TryRead(ReadOnlySpan<byte> message, out ContractResult result)
        {
            result = null!;
            if (!TryBody(message, ReliableMessageId.ContractResult, out ContractResult? value) || value == null
                || value.Action > LastContractAction || value.Reason > LastContractReason)
                return false;
            result = value;
            return true;
        }

        public static bool TryRead(ReadOnlySpan<byte> message, out ContractList list)
        {
            list = null!;
            if (!TryBody(message, ReliableMessageId.ContractList, out ContractList? value) || value == null
                || value.Reason > LastContractReason || !ValidContractRows(value.Rows, MaxMyContracts))
                return false;
            list = value;
            return true;
        }

        public static bool TryRead(ReadOnlySpan<byte> message, out PortfolioRequest request)
        {
            request = null!;
            if (!TryBody(message, ReliableMessageId.PortfolioRequest, out PortfolioRequest? value) || value == null
                || value.Action == PortfolioAction.None || value.Action > LastPortfolioAction || value.ContractId < 0
                || value.InstanceId < 0 || value.ShowcaseSlot > MaxShowcase)
                return false;
            request = value;
            return true;
        }

        public static bool TryRead(ReadOnlySpan<byte> message, out PortfolioState state)
        {
            state = null!;
            if (!TryBody(message, ReliableMessageId.PortfolioState, out PortfolioState? value) || value == null
                || value.Reason > LastContractReason || value.Counters == null || value.Counters.Length > MaxPortfolioCounters
                || value.Showcase == null || value.Showcase.Length > MaxShowcase)
                return false;
            foreach (var counter in value.Counters)
            {
                if (counter == null || counter.Outcome > LastPortfolioOutcome)
                    return false;
            }
            foreach (var entry in value.Showcase)
            {
                var item = entry?.Item;
                if (item == null || entry!.ShowcaseSlot == 0 || entry.ShowcaseSlot > MaxShowcase || item.Gems == null
                    || item.Gems.Length > MaxGems || item.CraftedStats == null || item.CraftedStats.Length > MaxCraftedStats
                    || Array.IndexOf(item.CraftedStats, null) >= 0)
                    return false;
            }
            state = value;
            return true;
        }

        /// <summary>At most <see cref="MaxForgeInputs"/> slots, each with a known supplier.</summary>
        private static bool ValidContractSlots(ContractSlot[]? slots)
        {
            if (slots == null || slots.Length > MaxForgeInputs)
                return false;
            foreach (var slot in slots)
            {
                if (slot == null || slot.Supplier > LastContractSupplier)
                    return false;
            }
            return true;
        }

        /// <summary>At most <paramref name="max"/> rows, each a real contract with known enums and valid slots.</summary>
        private static bool ValidContractRows(ContractEntry[]? rows, int max)
        {
            if (rows == null || rows.Length > max)
                return false;
            foreach (var row in rows)
            {
                if (row == null || row.ContractId <= 0 || row.Status > LastContractStatus || row.Role > LastContractRole
                    || row.Kind > LastForgeKind || !ValidContractSlots(row.Slots))
                    return false;
            }
            return true;
        }
    }
}
