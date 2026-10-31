using System;
using MemoryPack;

namespace Shared.Protocol
{
    /// <summary>What a forge session makes: a mold craft (<c>MoldDef</c>) or a recipe (<c>RecipeDef</c>). Append only.</summary>
    public enum ForgeKind : byte
    {
        None = 0,
        Mold = 1,
        Recipe = 2,
    }

    /// <summary>
    /// Where a forge session is (spec 5.3 "ForgeSession state machine"; WP-M3-6). <see cref="Running"/> covers the spec's
    /// <c>MaterialsReserved</c> and <c>Running</c> (the inputs are reserved as the session starts); <see cref="Finishing"/>
    /// is the wait for the save that commits the result (rule 5). Append only.
    /// </summary>
    public enum ForgePhase : byte
    {
        /// <summary>No session.</summary>
        None = 0,
        Running = 1,
        Finishing = 2,
        Finished = 3,
        Cancelled = 4,
    }

    /// <summary>
    /// Why the server refused a forge request or cancelled a session (spec 5.3 M3 implementation, spec 9.7 reason codes;
    /// WP-M3-6). Append only; <see cref="ReliableMessages.LastForgeReason"/> is the highest. Names shared with other enums
    /// share their <c>ReasonCodeStrings</c> row (<c>InCombat</c>, <c>JobLevelTooLow</c>, <c>ItemReserved</c>, <c>BagFull</c>).
    /// </summary>
    public enum ForgeReason : byte
    {
        None = 0,
        /// <summary>The forge tables or the character's inventory are not loaded on this world (yet).</summary>
        ForgeUnavailable = 1,
        /// <summary>The station is not visible, does not offer this kind, or is out of interaction range.</summary>
        StationOutOfReach = 2,
        /// <summary>In combat or during an action (spec 5.3 M3 decision 7: crafting happens out of combat).</summary>
        InCombat = 3,
        /// <summary>No mold or recipe has that net id.</summary>
        UnknownRecipe = 4,
        /// <summary>A known mold that makes nothing yet (the Jeweler molds until WP-M3-9).</summary>
        RecipeUnavailable = 5,
        /// <summary>The mold or recipe belongs to the other station.</summary>
        WrongStation = 6,
        /// <summary>The character does not hold the mold's or recipe's job.</summary>
        JobRequired = 7,
        /// <summary>The job level is below the mold's or recipe's <c>minJobLevel</c>.</summary>
        JobLevelTooLow = 8,
        /// <summary>The mold is not in the Safe bag's equipment area (R-5.3-01: a mold is required).</summary>
        MoldRequired = 9,
        /// <summary>An input is not a material the slot takes (tag or tier), or the start names the wrong number of inputs.</summary>
        SlotMaterialInvalid = 10,
        /// <summary>An input stack holds fewer free units than its slot needs.</summary>
        MaterialsMissing = 11,
        /// <summary>An input or the mold is held by another reservation.</summary>
        ItemReserved = 12,
        /// <summary>A forge session is already running for this character.</summary>
        SessionActive = 13,
        /// <summary>No session runs (or it is finishing), so the action, finish or cancel has nothing to act on.</summary>
        NoSession = 14,
        /// <summary>The mold does not allow that action, or a recipe takes none.</summary>
        ActionNotAllowed = 15,
        /// <summary>The session used <c>ForgeSettings.maxActions</c> already.</summary>
        ActionLimitReached = 16,
        /// <summary>The character converted into that tier <c>ConversionOdds.dailyCap</c> times this UTC day.</summary>
        ConversionCapReached = 17,
        /// <summary>The output would not fit; the session keeps running.</summary>
        BagFull = 18,
        /// <summary>The session ended because the character left the station's range.</summary>
        LeftStation = 19,
        /// <summary>Making a tier 2 or 3 mold needs the job tree's Master Molds node (job-tree design; using one needs only the level).</summary>
        MoldTierLocked = 20,
    }

    /// <summary>One input of a <see cref="ForgeStart"/>: the bag slot one mold slot or recipe input takes its units from.</summary>
    [MemoryPackable]
    public sealed partial class ForgeInput
    {
        /// <summary>A container id (the Normal bag in M3).</summary>
        public byte Container { get; set; }

        public byte Slot { get; set; }
    }

    /// <summary>
    /// Client to server, reliable (spec 5.3, 9.13; WP-M3-6, protocol 31): start a forge session at the station
    /// <see cref="StationEntityId"/> for the mold or recipe <see cref="DefNetId"/>, one <see cref="ForgeInput"/> per mold slot
    /// or recipe input in table order. The inputs (and the mold) are reserved at once. Answered by a <see cref="ForgeState"/>
    /// with the same <see cref="RequestId"/>. Capped by its <c>ReliableMessageCaps</c> row.
    /// </summary>
    [MemoryPackable]
    public sealed partial class ForgeStart
    {
        public uint RequestId { get; set; }

        public uint StationEntityId { get; set; }

        public ForgeKind Kind { get; set; }

        public ushort DefNetId { get; set; }

        public ForgeInput[] Inputs { get; set; } = Array.Empty<ForgeInput>();
    }

    /// <summary>Client to server, reliable (WP-M3-6): use the forge action <see cref="ActionNetId"/> (a <c>ForgeActionDef</c>).</summary>
    [MemoryPackable]
    public sealed partial class ForgeAction
    {
        public uint RequestId { get; set; }

        public byte ActionNetId { get; set; }
    }

    /// <summary>
    /// Client to server, reliable (WP-M3-6): finish the session. Answered by a <see cref="ForgeResult"/>: a refusal at once,
    /// the item only once its save committed (rule 5). A repeat while it saves changes nothing.
    /// </summary>
    [MemoryPackable]
    public sealed partial class ForgeFinish
    {
        public uint RequestId { get; set; }
    }

    /// <summary>Client to server, reliable (WP-M3-6): cancel the session; the inputs are released, nothing is consumed.</summary>
    [MemoryPackable]
    public sealed partial class ForgeCancel
    {
        public uint RequestId { get; set; }
    }

    /// <summary>
    /// Server to client, reliable (WP-M3-6, protocol 31): the observer's forge session. The answer to a
    /// <see cref="ForgeStart"/>, <see cref="ForgeAction"/> or <see cref="ForgeCancel"/> (its <see cref="RequestId"/>, and
    /// <see cref="Reason"/> for a refusal), and sent unasked with <see cref="RequestId"/> 0 when the server cancels it
    /// (leaving the station, combat). The quality is a range, never a rolled number: every random number is drawn at
    /// Finish (M3 plan risk 1). MemoryPack after the id byte; append fields only (spec 11.2).
    /// </summary>
    [MemoryPackable]
    public sealed partial class ForgeState
    {
        /// <summary>Counts per connection; a newer state replaces an older one.</summary>
        public uint Version { get; set; }

        public uint RequestId { get; set; }

        public ForgeReason Reason { get; set; }

        public ForgePhase Phase { get; set; }

        public ForgeKind Kind { get; set; }

        public ushort DefNetId { get; set; }

        public uint StationEntityId { get; set; }

        /// <summary>The actions used so far, in order (<c>ForgeActionDef.netId</c>).</summary>
        public byte[] Actions { get; set; } = Array.Empty<byte>();

        public byte MaxActions { get; set; }

        public ushort QualityMin { get; set; }

        public ushort QualityMax { get; set; }

        /// <summary>The <c>RarityDef.tier</c> of <see cref="QualityMin"/> and <see cref="QualityMax"/>.</summary>
        public byte RarityMin { get; set; }

        public byte RarityMax { get; set; }
    }

    /// <summary>
    /// Server to client, reliable (WP-M3-6, protocol 31): the answer to a <see cref="ForgeFinish"/>. A refusal comes at once
    /// (the session keeps running); a result only once its save committed (rule 5), right before the
    /// <c>InventoryContents</c> that shows it. A failed conversion has <see cref="Success"/> false and no item (R-5.2-04).
    /// </summary>
    [MemoryPackable]
    public sealed partial class ForgeResult
    {
        public uint RequestId { get; set; }

        public ForgeReason Reason { get; set; }

        public ForgeKind Kind { get; set; }

        public ushort DefNetId { get; set; }

        public ushort OutputItemNetId { get; set; }

        public ushort Count { get; set; }

        /// <summary>The <c>RarityDef.tier</c> of the item.</summary>
        public byte Rarity { get; set; }

        public ushort Quality { get; set; }

        /// <summary>The new instance (equipment, a tool or a mold); 0 for a stack.</summary>
        public long InstanceId { get; set; }

        public bool Success { get; set; }

        /// <summary>A failed conversion whose inputs the job tree's keep roll kept (nothing consumed).</summary>
        public bool InputsKept { get; set; }
    }

    public static partial class ReliableMessages
    {
        /// <summary>Inputs a start names at most (<c>ForgeStations.MaxInputs</c>).</summary>
        public const int MaxForgeInputs = 8;

        /// <summary>Actions a state lists at most (<c>ForgeStations.MaxSessionActions</c>).</summary>
        public const int MaxForgeActions = 32;

        public const ForgeKind LastForgeKind = ForgeKind.Recipe;
        public const ForgePhase LastForgePhase = ForgePhase.Cancelled;
        public const ForgeReason LastForgeReason = ForgeReason.MoldTierLocked;

        public static bool TryRead(ReadOnlySpan<byte> message, out ForgeStart start)
        {
            start = null!;
            if (!TryBody(message, ReliableMessageId.ForgeStart, out ForgeStart? value) || value == null
                || value.Kind == ForgeKind.None || value.Kind > LastForgeKind || value.DefNetId == 0
                || value.Inputs == null || value.Inputs.Length > MaxForgeInputs)
                return false;
            foreach (var input in value.Inputs)
            {
                if (input == null)
                    return false;
            }
            start = value;
            return true;
        }

        public static bool TryRead(ReadOnlySpan<byte> message, out ForgeAction action)
        {
            action = null!;
            if (!TryBody(message, ReliableMessageId.ForgeAction, out ForgeAction? value) || value == null || value.ActionNetId == 0)
                return false;
            action = value;
            return true;
        }

        public static bool TryRead(ReadOnlySpan<byte> message, out ForgeFinish finish)
        {
            finish = null!;
            if (!TryBody(message, ReliableMessageId.ForgeFinish, out ForgeFinish? value) || value == null)
                return false;
            finish = value;
            return true;
        }

        public static bool TryRead(ReadOnlySpan<byte> message, out ForgeCancel cancel)
        {
            cancel = null!;
            if (!TryBody(message, ReliableMessageId.ForgeCancel, out ForgeCancel? value) || value == null)
                return false;
            cancel = value;
            return true;
        }

        public static bool TryRead(ReadOnlySpan<byte> message, out ForgeState state)
        {
            state = null!;
            if (!TryBody(message, ReliableMessageId.ForgeState, out ForgeState? value) || value == null
                || value.Reason > LastForgeReason || value.Phase > LastForgePhase || value.Kind > LastForgeKind
                || value.Actions == null || value.Actions.Length > MaxForgeActions || value.QualityMin > value.QualityMax)
                return false;
            state = value;
            return true;
        }

        public static bool TryRead(ReadOnlySpan<byte> message, out ForgeResult result)
        {
            result = null!;
            if (!TryBody(message, ReliableMessageId.ForgeResult, out ForgeResult? value) || value == null
                || value.Reason > LastForgeReason || value.Kind > LastForgeKind)
                return false;
            result = value;
            return true;
        }
    }
}
