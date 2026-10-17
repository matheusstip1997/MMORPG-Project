using System;

namespace Shared.Protocol
{
    /// <summary>
    /// First byte of every hot message on the unreliable channel (spec 1.7, 11.2). The layouts are versioned by
    /// <see cref="ProtocolVersion"/>, which the handshake checks, so hot packets carry no version of their own.
    /// </summary>
    public enum HotMessageId : byte
    {
        None = 0,
        Input = 1,
        Snapshot = 2,
        // 3 is reserved for projectile spawns (backlog D3).
    }

    public static class HotMessage
    {
        public const int IdBits = 8;

        public static bool TryPeekId(ReadOnlySpan<byte> source, out HotMessageId id)
        {
            id = source.Length > 0 ? (HotMessageId)source[0] : HotMessageId.None;
            return id == HotMessageId.Input || id == HotMessageId.Snapshot;
        }
    }
}
