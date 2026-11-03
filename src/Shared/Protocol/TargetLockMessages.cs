using System;
using MemoryPack;

namespace Shared.Protocol
{
    /// <summary>
    /// Client to server, reliable (R-2.2-06, M3 decision 15; protocol 39): the entity the player locked with Tab, 0 when the
    /// lock is cleared, sent on every change. The frames carry whether a lock is on (<see cref="InputFrame.Locked"/>), so the
    /// server switches the movement rule on the same frame as the prediction; this message only names the target the server
    /// checks a locked frame's facing against (<c>StartingValuesM1.lockFacingToleranceDeg</c>). MemoryPack after the
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
