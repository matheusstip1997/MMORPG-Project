using System;
using System.Buffers.Binary;

namespace Shared.Protocol
{
    /// <summary>
    /// First byte of every game message on the reliable channel (spec 1.7). Versioned by <see cref="ProtocolVersion"/>.
    /// Append only, never renumber.
    /// </summary>
    public enum ReliableMessageId : byte
    {
        None = 0,
        ActionResult = 1,
        /// <summary>The display name of a player entity (<see cref="EntityIdentity"/>, protocol 10).</summary>
        EntityIdentity = 2,
        /// <summary>Client to server: pick up a ground drop (<see cref="PickupRequest"/>).</summary>
        PickupRequest = 3,
        /// <summary>Server to client: a pickup was refused, or how much it took (<see cref="PickupResult"/>).</summary>
        PickupResult = 4,
        /// <summary>Server to client: the bag as committed to the database (<see cref="InventoryContents"/>).</summary>
        InventoryContents = 5,
        /// <summary>Client to server: a local chat line (<see cref="ChatSendMessage"/>, MemoryPack body; protocol 12).</summary>
        ChatSend = 6,
        /// <summary>Server to client: a chat line from a player the recipient sees (<see cref="ChatLineMessage"/>).</summary>
        ChatMessage = 7,
        /// <summary>Server to client: the server refused the client's chat line (<see cref="ChatRefusedMessage"/>).</summary>
        ChatRefused = 8,
        /// <summary>Client to server: move a stack inside the bag (<see cref="InventoryMoveRequest"/>, protocol 13).</summary>
        InventoryMoveRequest = 9,
        /// <summary>Server to client: the answer to a bag move (<see cref="InventoryMoveResult"/>, protocol 13).</summary>
        InventoryMoveResult = 10,
        /// <summary>Client to server: interact with an NPC (<see cref="OutpostInteractRequest"/>, protocol 17).</summary>
        OutpostInteractRequest = 11,
        /// <summary>Server to client: the answer to an NPC interaction (<see cref="OutpostInteractResult"/>).</summary>
        OutpostInteractResult = 12,
        /// <summary>Client to server: quote a sale at an outpost (<see cref="SellQuoteRequest"/>).</summary>
        SellQuoteRequest = 13,
        /// <summary>Server to client: the quote (<see cref="SellQuoteResult"/>).</summary>
        SellQuoteResult = 14,
        /// <summary>Client to server: sell at an outpost (<see cref="SellRequest"/>, class 1).</summary>
        SellRequest = 15,
        /// <summary>Server to client: the sale's answer (<see cref="SellResult"/>).</summary>
        SellResult = 16,
        /// <summary>Client to server: buy from an NPC vendor (<see cref="VendorBuyRequest"/>, class 1).</summary>
        VendorBuyRequest = 17,
        /// <summary>Server to client: the purchase's answer (<see cref="VendorBuyResult"/>).</summary>
        VendorBuyResult = 18,
        /// <summary>Server to client: the gold as committed (<see cref="GoldBalance"/>).</summary>
        GoldBalance = 19,
        /// <summary>Server to client: the observer's level, exp, rank and skill points (<see cref="ProgressionState"/>, WP-M2-3, protocol 18).</summary>
        ProgressionState = 20,
        /// <summary>Server to client: the observer's own final stats and meters (<see cref="OwnStats"/>, WP-M2-2, protocol 19).</summary>
        OwnStats = 21,
        /// <summary>Server to client: the observer's class, learned skills and hotbar (<see cref="SkillTreeState"/>, WP-M2-5, protocol 20).</summary>
        SkillTreeState = 22,
        /// <summary>Client to server: learn, upgrade, evolve, respec, advance or a hotbar change (<see cref="SkillTreeRequest"/>).</summary>
        SkillTreeRequest = 23,
        /// <summary>Server to client: the answer to a <see cref="SkillTreeRequest"/>.</summary>
        SkillTreeResult = 24,
        /// <summary>Server to client: the observer's Bystial book (<see cref="BookState"/>, WP-M2-8, protocol 21).</summary>
        BookState = 25,
        /// <summary>Client to server: place, swap or take out a card (<see cref="BookChangeRequest"/>).</summary>
        BookChangeRequest = 26,
        /// <summary>Server to client: the answer to a book change (<see cref="BookChangeResult"/>).</summary>
        BookChangeResult = 27,
    }

    /// <summary>
    /// Why the server refused an action, or how a parry met a hit (spec 9.7 reason codes; the client shows the text from
    /// <c>ReasonCodeStrings</c>). Append only, never renumber; <see cref="ActionResult.LastReason"/> is the highest.
    /// </summary>
    public enum ActionReason : byte
    {
        None = 0,
        /// <summary>The action's cooldown has not run out (R-3.1-02).</summary>
        OnCooldown = 1,
        /// <summary>The hotbar slot holds no learned skill (spec 3.1 flow: "Skill learned and on hotbar?"); also a skill-tree
        /// refusal (<c>SkillTreeReason</c>: the skill is not learned).</summary>
        SkillNotLearned = 2,
        /// <summary>Less stamina than the action costs (R-3.1-02, R-3.1-04).</summary>
        NoStamina = 3,
        /// <summary>The dodge pool has no charge (R-3.3-02).</summary>
        DodgeNoCharge = 4,
        /// <summary>
        /// The parry pool has no charge (R-3.3-03): a refused parry press, and also the parry result <c>NoCharge</c>
        /// when a hit lands where that parry would have been.
        /// </summary>
        ParryNoCharge = 5,
        /// <summary>Parry result (spec 3.3 ParrySystem): the parry blocked the hit (R-3.3-04).</summary>
        ParrySuccess = 6,
        /// <summary>Parry result: an uncolored attack, damage reduced (R-3.3-04).</summary>
        ParryReduced = 7,
        /// <summary>Parry result: a red attack cannot be parried (R-3.3-04).</summary>
        ParryFailedRed = 8,
        /// <summary>Parry result: a yellow or blue attack needs a heavier armor class (R-3.3-04, R-3.3-05).</summary>
        ParryFailedArmor = 9,
        /// <summary>
        /// Parry result: the hit landed outside the parry window (R-3.3-03). Sent up to protocol 14; from protocol 15 the
        /// server says which side instead (<see cref="ParryFailedEarly"/>, <see cref="ParryFailedLate"/>).
        /// </summary>
        ParryFailedTiming = 10,
        /// <summary>Less MP than the skill costs (R-3.1-04: skills cost a small amount of MP).</summary>
        NoMp = 11,
        /// <summary>Parry result (protocol 15): the hit landed in the parry's Recovery, its window had closed: pressed too early.</summary>
        ParryFailedEarly = 12,
        /// <summary>Parry result (protocol 15): the hit landed in the parry's Startup, its window had not opened: pressed too late.</summary>
        ParryFailedLate = 13,
        /// <summary>R-3.5-07: the slot's skill needs a weapon type the equipped weapon set does not have (WP-M2-5, protocol 20).</summary>
        WrongWeapon = 14,
    }

    /// <summary>
    /// Server to client, reliable: the action an input asked for was refused (spec 1.7, 9.4 <c>ActionResult</c>), or a
    /// parry met a hit (a parry reason, spec 9.7: the parry result text). Started actions need no message: snapshots carry
    /// the action state. A press that is dropped (during another action, outside its buffer window) is not a refusal and
    /// gets none either. A parry result carries the input sequence that started the parry and the parry's action id.
    /// Layout (little endian): byte id, ushort inputSequence, ushort actionNetId (0 when the slot is empty),
    /// byte skillSlot (0 when not a skill), byte reason.
    /// </summary>
    public static class ActionResult
    {
        public const int Size = 7;

        /// <summary>The highest <see cref="ActionReason"/> a message may carry.</summary>
        public const ActionReason LastReason = ActionReason.WrongWeapon;

        /// <summary>True for the reasons that report a parry result rather than a refused press.</summary>
        public static bool IsParryResult(ActionReason reason) =>
            (reason >= ActionReason.ParrySuccess && reason <= ActionReason.ParryFailedTiming)
            || reason == ActionReason.ParryFailedEarly || reason == ActionReason.ParryFailedLate;

        public static int Write(Span<byte> destination, ushort inputSequence, ushort actionNetId, byte skillSlot, ActionReason reason)
        {
            if (destination.Length < Size)
                throw new ArgumentException("Destination is too small.", nameof(destination));
            destination[0] = (byte)ReliableMessageId.ActionResult;
            BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(1), inputSequence);
            BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(3), actionNetId);
            destination[5] = skillSlot;
            destination[6] = (byte)reason;
            return Size;
        }

        /// <summary>False, without throwing, for anything malformed (R-2.5-04).</summary>
        public static bool TryRead(ReadOnlySpan<byte> source, out ushort inputSequence, out ushort actionNetId,
            out byte skillSlot, out ActionReason reason)
        {
            inputSequence = 0;
            actionNetId = 0;
            skillSlot = 0;
            reason = ActionReason.None;
            if (source.Length != Size || source[0] != (byte)ReliableMessageId.ActionResult)
                return false;
            var code = (ActionReason)source[6];
            if (code == ActionReason.None || code > LastReason)
                return false;
            inputSequence = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(1));
            actionNetId = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(3));
            skillSlot = source[5];
            reason = code;
            return true;
        }
    }
}
