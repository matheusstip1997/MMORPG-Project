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
    }

    /// <summary>
    /// Why the server refused an action (spec 9.7 reason codes; the client shows the text from <c>ReasonCodeStrings</c>).
    /// Append only, never renumber. TODO(D4): <c>NoStamina</c>, <c>DodgeNoCharge</c>, <c>ParryNoCharge</c>.
    /// </summary>
    public enum ActionReason : byte
    {
        None = 0,
        /// <summary>The action's cooldown has not run out (R-3.1-02).</summary>
        OnCooldown = 1,
        /// <summary>The hotbar slot holds no skill (spec 3.1 flow: "Skill learned and on hotbar?").</summary>
        SkillNotLearned = 2,
    }

    /// <summary>
    /// Server to client, reliable: the action an input asked for was refused (spec 1.7, 9.4 <c>ActionResult</c>). Started
    /// actions need no message: snapshots carry the action state. A press that is dropped (during another action, outside
    /// its buffer window) is not a refusal and gets none either.
    /// Layout (little endian): byte id, ushort inputSequence, ushort actionNetId (0 when the slot is empty),
    /// byte skillSlot (0 when not a skill), byte reason.
    /// </summary>
    public static class ActionResult
    {
        public const int Size = 7;

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
            if (code == ActionReason.None || code > ActionReason.SkillNotLearned)
                return false;
            inputSequence = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(1));
            actionNetId = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(3));
            skillSlot = source[5];
            reason = code;
            return true;
        }
    }
}
