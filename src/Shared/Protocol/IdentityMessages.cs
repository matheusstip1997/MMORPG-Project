using System;
using System.Buffers.Binary;
using System.Text;

namespace Shared.Protocol
{
    /// <summary>
    /// Server to client, reliable: the name a client shows for a player entity (spec 9.3 <c>nameplate</c>), already passed
    /// through <c>DisplayIdentity(entity, observer)</c> on the server (architecture rule 8), so a disguised player arrives
    /// with a generic name. Sent when the player enters the observer's interest (and to a player for itself once), so a
    /// hidden player's name is never sent (rule 9). Snapshots never carry names.
    /// Layout (little endian): byte id, uint entityId, ushort nameLength, nameLength bytes of UTF-8.
    /// </summary>
    public static class EntityIdentity
    {
        /// <summary>4 bytes per character for the longest name <c>CharacterCreationConfig.nameMaxLength</c> allows (64).</summary>
        public const int MaxNameBytes = 4 * 64;

        public const int HeaderSize = 1 + 4 + 2;

        public const int MaxSize = HeaderSize + MaxNameBytes;

        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        /// <summary>Writes the message and returns its length. Throws for an empty name or one longer than <see cref="MaxNameBytes"/>.</summary>
        public static int Write(Span<byte> destination, uint entityId, string name)
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("A name is required.", nameof(name));
            int nameBytes = StrictUtf8.GetByteCount(name);
            if (nameBytes > MaxNameBytes)
                throw new ArgumentException("The name is too long.", nameof(name));
            if (destination.Length < HeaderSize + nameBytes)
                throw new ArgumentException("Destination is too small.", nameof(destination));
            destination[0] = (byte)ReliableMessageId.EntityIdentity;
            BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(1), entityId);
            BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(5), (ushort)nameBytes);
            StrictUtf8.GetBytes(name.AsSpan(), destination.Slice(HeaderSize, nameBytes));
            return HeaderSize + nameBytes;
        }

        /// <summary>False, without throwing, for anything malformed: wrong id, bad length, empty name or invalid UTF-8 (R-2.5-04).</summary>
        public static bool TryRead(ReadOnlySpan<byte> source, out uint entityId, out string name)
        {
            entityId = 0;
            name = string.Empty;
            if (source.Length < HeaderSize + 1 || source[0] != (byte)ReliableMessageId.EntityIdentity)
                return false;
            int nameBytes = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(5));
            if (nameBytes == 0 || nameBytes > MaxNameBytes || source.Length != HeaderSize + nameBytes)
                return false;
            try
            {
                name = StrictUtf8.GetString(source.Slice(HeaderSize, nameBytes));
            }
            catch (DecoderFallbackException)
            {
                name = string.Empty;
                return false;
            }
            entityId = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(1));
            return true;
        }
    }
}
