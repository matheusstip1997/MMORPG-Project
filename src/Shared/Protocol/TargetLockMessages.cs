using System;
using MemoryPack;

namespace Shared.Protocol
{
    /// <summary>
    /// Client to server, reliable (R-2.2-06; protocol 39, M3 decision 16 since protocol 40): the entity the player locked with
    /// Tab, 0 when the lock is cleared, sent on every change. The frames carry whether a lock is on
    /// (<see cref="InputFrame.Locked"/>), so the server aims on the same frame as the prediction; this message names the
    /// target the server aims a locked frame's presses at (from the target's position on the frame's view clock; a target
    /// the server does not know, dead or out of this player's view leaves the press at the mouse). MemoryPack after the
    /// <see cref="ReliableMessageId"/> byte; append fields only.
    /// </summary>
    [MemoryPackable]
    public sealed partial class TargetLockChanged
    {
        public uint TargetEntityId { get; set; }
    }

    public static partial class ReliableMessages
    {
        public static bool TryRead(ReadOnlySpan<byte> message, out TargetLockChanged change)
        {
            change = null!;
            if (!TryBody(message, ReliableMessageId.TargetLockChanged, out TargetLockChanged? value) || value == null)
                return false;
            change = value;
            return true;
        }
    }
}
