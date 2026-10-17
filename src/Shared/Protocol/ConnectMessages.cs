using System;
using System.Buffers.Binary;

namespace Shared.Protocol
{
    /// <summary>
    /// Data sent with the transport connection request (R-2.5-01).
    /// Layout (little endian): ushort protocolVersion, ushort ticketLength, byte[ticketLength] ticket.
    /// The ticket is opaque to the client: it comes from the Auth API.
    /// </summary>
    public static class ConnectRequest
    {
        public const int HeaderSize = 4;

        /// <summary>Upper bound for the ticket; decoders bound-check every length (R-2.5-04).</summary>
        public const int MaxTicketLength = 256;

        public static int SizeFor(int ticketLength) => HeaderSize + ticketLength;

        /// <summary>Writes the request into <paramref name="destination"/> and returns the bytes written.</summary>
        public static int Write(Span<byte> destination, ushort protocolVersion, ReadOnlySpan<byte> ticket)
        {
            if (ticket.Length > MaxTicketLength)
                throw new ArgumentException($"Ticket is longer than {MaxTicketLength} bytes.", nameof(ticket));
            if (destination.Length < SizeFor(ticket.Length))
                throw new ArgumentException("Destination is too small.", nameof(destination));

            BinaryPrimitives.WriteUInt16LittleEndian(destination, protocolVersion);
            BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(2), (ushort)ticket.Length);
            ticket.CopyTo(destination.Slice(HeaderSize));
            return SizeFor(ticket.Length);
        }

        /// <summary>Reads the protocol version alone, so a version mismatch is reported even if the rest changed.</summary>
        public static bool TryReadVersion(ReadOnlySpan<byte> source, out ushort protocolVersion)
        {
            protocolVersion = 0;
            if (source.Length < 2)
                return false;
            protocolVersion = BinaryPrimitives.ReadUInt16LittleEndian(source);
            return true;
        }

        public static bool TryRead(ReadOnlySpan<byte> source, out ushort protocolVersion, out ReadOnlySpan<byte> ticket)
        {
            ticket = default;
            if (!TryReadVersion(source, out protocolVersion) || source.Length < HeaderSize)
                return false;

            int ticketLength = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(2));
            if (ticketLength > MaxTicketLength || source.Length != HeaderSize + ticketLength)
                return false;

            ticket = source.Slice(HeaderSize, ticketLength);
            return true;
        }
    }

    /// <summary>
    /// Data sent back when a connection is refused.
    /// Layout (little endian): byte reason, ushort serverProtocolVersion.
    /// </summary>
    public static class ConnectReject
    {
        public const int Size = 3;

        public static void Write(Span<byte> destination, ConnectRejectReason reason, ushort serverProtocolVersion)
        {
            destination[0] = (byte)reason;
            BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(1), serverProtocolVersion);
        }

        public static bool TryRead(ReadOnlySpan<byte> source, out ConnectRejectReason reason, out ushort serverProtocolVersion)
        {
            reason = ConnectRejectReason.None;
            serverProtocolVersion = 0;
            if (source.Length != Size)
                return false;
            reason = (ConnectRejectReason)source[0];
            serverProtocolVersion = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(1));
            return true;
        }
    }
}
