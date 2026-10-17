using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Shared.Security
{
    public enum SessionRole : byte
    {
        Client,
        Server,
    }

    public enum OpenResult : byte
    {
        Ok,
        /// <summary>Too short, or the destination cannot hold the payload.</summary>
        Malformed,
        /// <summary>Sequence number already accepted, too old, or 0.</summary>
        Replayed,
        /// <summary>Tag does not match: tampered, wrong key, or wrong direction.</summary>
        AuthenticationFailed,
    }

    /// <summary>
    /// Per-session channel crypto (R-2.5-02), shared by the server and the Unity client so both sides use one
    /// definition of the framing. The session key from the ticket is split with HKDF-SHA256 into one AEAD key per
    /// direction and one MAC key for client inputs, so a packet sent one way never opens the other way.
    /// <para>
    /// Reliable messages: <c>[u64 sequence][ChaCha20-Poly1305 ciphertext][16-byte tag]</c>; the sequence bytes are the
    /// associated data. The nonce is the sequence (4 zero bytes, then the u64 little endian), so it never repeats under
    /// one key: the send counter only goes up and sealing throws before it would wrap. The receiver keeps a
    /// <see cref="ReplayWindow"/>.
    /// </para>
    /// <para>
    /// Client inputs on the unreliable channel are not encrypted but tagged: <c>[u32 sequence][payload][8-byte tag]</c>,
    /// where the tag is HMAC-SHA256 over sequence and payload, truncated, and checked in constant time. They have their
    /// own replay window.
    /// </para>
    /// The AEAD comes from a factory: the server passes the platform ChaCha20-Poly1305 (fast), the Unity client
    /// <see cref="ManagedChaCha20Poly1305"/>. Sealing and tagging may be called from any thread. Opening and verifying
    /// must stay on one thread (the network thread).
    /// </summary>
    public sealed class SessionCrypto : IDisposable
    {
        public const int ReliableHeaderSize = 8;
        public const int ReliableTagSize = 16;
        public const int ReliableOverhead = ReliableHeaderSize + ReliableTagSize;
        public const int InputHeaderSize = 4;
        public const int InputTagSize = 8;
        public const int InputOverhead = InputHeaderSize + InputTagSize;

        public const int KeySize = 32;
        private const int AeadNonceSize = 12;
        private const int FullMacSize = 32;

        private static readonly byte[] ClientToServerInfo = Encoding.ASCII.GetBytes("mmorpg/session/v1/c2s-reliable");
        private static readonly byte[] ServerToClientInfo = Encoding.ASCII.GetBytes("mmorpg/session/v1/s2c-reliable");
        private static readonly byte[] InputMacInfo = Encoding.ASCII.GetBytes("mmorpg/session/v1/c2s-input-mac");

        private readonly SessionRole _role;
        private readonly IAead _sealer;
        private readonly IAead _opener;
        private readonly HMACSHA256 _inputMac;
        private readonly object _sendLock = new object();
        private ulong _reliableSendSequence;
        private uint _inputSendSequence;
        private ReplayWindow _reliableReplay;
        private ReplayWindow _inputReplay;
        private bool _disposed;

        public SessionCrypto(ReadOnlySpan<byte> sessionKey, SessionRole role, AeadFactory createAead)
            : this(sessionKey, role, createAead, reliableSendSequence: 0, inputSendSequence: 0)
        {
        }

        /// <summary>Starts the send counters elsewhere than 0, so tests can reach the end of the sequence space.</summary>
        internal SessionCrypto(ReadOnlySpan<byte> sessionKey, SessionRole role, AeadFactory createAead, ulong reliableSendSequence, uint inputSendSequence)
        {
            if (sessionKey.Length != KeySize)
                throw new ArgumentException($"Session key must be {KeySize} bytes.", nameof(sessionKey));
            if (createAead == null)
                throw new ArgumentNullException(nameof(createAead));

            _role = role;
            _reliableSendSequence = reliableSendSequence;
            _inputSendSequence = inputSendSequence;

            Span<byte> clientToServer = stackalloc byte[KeySize];
            Span<byte> serverToClient = stackalloc byte[KeySize];
            HkdfSha256.DeriveKey(sessionKey, ReadOnlySpan<byte>.Empty, ClientToServerInfo, clientToServer);
            HkdfSha256.DeriveKey(sessionKey, ReadOnlySpan<byte>.Empty, ServerToClientInfo, serverToClient);
            _sealer = createAead(role == SessionRole.Client ? clientToServer : serverToClient);
            _opener = createAead(role == SessionRole.Client ? serverToClient : clientToServer);
            CryptographicOperations.ZeroMemory(clientToServer);
            CryptographicOperations.ZeroMemory(serverToClient);

            var inputMacKey = new byte[KeySize];
            HkdfSha256.DeriveKey(sessionKey, ReadOnlySpan<byte>.Empty, InputMacInfo, inputMacKey);
            _inputMac = new HMACSHA256(inputMacKey);
            CryptographicOperations.ZeroMemory(inputMacKey);
        }

        /// <summary>Encrypts <paramref name="plaintext"/> into <paramref name="destination"/> and returns the bytes written.</summary>
        public int SealReliable(ReadOnlySpan<byte> plaintext, Span<byte> destination)
        {
            var size = plaintext.Length + ReliableOverhead;
            if (destination.Length < size)
                throw new ArgumentException("Destination is too small.", nameof(destination));

            Span<byte> nonce = stackalloc byte[AeadNonceSize];
            lock (_sendLock)
            {
                ThrowIfDisposed();
                if (_reliableSendSequence == ulong.MaxValue)
                    throw new InvalidOperationException("Reliable sequence numbers are exhausted; the session must reconnect.");
                var sequence = ++_reliableSendSequence;
                BinaryPrimitives.WriteUInt64LittleEndian(destination, sequence);
                WriteNonce(nonce, sequence);
                _sealer.Seal(
                    nonce,
                    plaintext,
                    destination.Slice(ReliableHeaderSize, plaintext.Length),
                    destination.Slice(ReliableHeaderSize + plaintext.Length, ReliableTagSize),
                    destination.Slice(0, ReliableHeaderSize));
            }
            return size;
        }

        /// <summary>
        /// Authenticates and decrypts a reliable message. On <see cref="OpenResult.Ok"/> the plaintext is in the first
        /// <paramref name="length"/> bytes of <paramref name="plaintext"/>.
        /// </summary>
        public OpenResult OpenReliable(ReadOnlySpan<byte> packet, Span<byte> plaintext, out int length)
        {
            length = 0;
            if (packet.Length < ReliableOverhead)
                return OpenResult.Malformed;
            var payloadLength = packet.Length - ReliableOverhead;
            if (plaintext.Length < payloadLength)
                return OpenResult.Malformed;

            var sequence = BinaryPrimitives.ReadUInt64LittleEndian(packet);
            if (!_reliableReplay.CanAccept(sequence))
                return OpenResult.Replayed;

            Span<byte> nonce = stackalloc byte[AeadNonceSize];
            WriteNonce(nonce, sequence);
            if (!_opener.TryOpen(
                    nonce,
                    packet.Slice(ReliableHeaderSize, payloadLength),
                    packet.Slice(ReliableHeaderSize + payloadLength, ReliableTagSize),
                    plaintext.Slice(0, payloadLength),
                    packet.Slice(0, ReliableHeaderSize)))
                return OpenResult.AuthenticationFailed;

            _reliableReplay.Mark(sequence);
            length = payloadLength;
            return OpenResult.Ok;
        }

        /// <summary>Client side: writes the tagged input packet into <paramref name="destination"/> and returns its size.</summary>
        public int TagInput(ReadOnlySpan<byte> payload, Span<byte> destination)
        {
            if (_role != SessionRole.Client)
                throw new InvalidOperationException("Only the client tags inputs.");
            var size = payload.Length + InputOverhead;
            if (destination.Length < size)
                throw new ArgumentException("Destination is too small.", nameof(destination));

            Span<byte> mac = stackalloc byte[FullMacSize];
            lock (_sendLock)
            {
                ThrowIfDisposed();
                if (_inputSendSequence == uint.MaxValue)
                    throw new InvalidOperationException("Input sequence numbers are exhausted; the session must reconnect.");
                BinaryPrimitives.WriteUInt32LittleEndian(destination, ++_inputSendSequence);
                payload.CopyTo(destination.Slice(InputHeaderSize));
                ComputeInputMac(destination.Slice(0, InputHeaderSize + payload.Length), mac);
            }
            mac.Slice(0, InputTagSize).CopyTo(destination.Slice(InputHeaderSize + payload.Length));
            return size;
        }

        /// <summary>Server side: checks the tag and sequence of a client input packet and returns the payload inside it.</summary>
        public OpenResult VerifyInput(ReadOnlySpan<byte> packet, out ReadOnlySpan<byte> payload)
        {
            payload = default;
            if (_role != SessionRole.Server)
                throw new InvalidOperationException("Only the server verifies inputs.");
            if (packet.Length < InputOverhead)
                return OpenResult.Malformed;

            var sequence = BinaryPrimitives.ReadUInt32LittleEndian(packet);
            if (!_inputReplay.CanAccept(sequence))
                return OpenResult.Replayed;

            var signedLength = packet.Length - InputTagSize;
            Span<byte> mac = stackalloc byte[FullMacSize];
            ComputeInputMac(packet.Slice(0, signedLength), mac);
            if (!CryptographicOperations.FixedTimeEquals(mac.Slice(0, InputTagSize), packet.Slice(signedLength)))
                return OpenResult.AuthenticationFailed;

            _inputReplay.Mark(sequence);
            payload = packet.Slice(InputHeaderSize, signedLength - InputHeaderSize);
            return OpenResult.Ok;
        }

        /// <summary>Call from the receiving thread. A concurrent seal either finishes first or throws <see cref="ObjectDisposedException"/>.</summary>
        public void Dispose()
        {
            lock (_sendLock)
            {
                if (_disposed)
                    return;
                _disposed = true;
                _sealer.Dispose();
            }
            _opener.Dispose();
            _inputMac.Dispose();
        }

        /// <summary>
        /// One HMAC instance is enough: a client session only tags (under the send lock) and a server session only
        /// verifies (on the network thread).
        /// </summary>
        private void ComputeInputMac(ReadOnlySpan<byte> data, Span<byte> mac)
        {
            if (!_inputMac.TryComputeHash(data, mac, out var written) || written != FullMacSize)
                throw new CryptographicException("HMAC-SHA256 did not produce a full hash.");
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(SessionCrypto));
        }

        private static void WriteNonce(Span<byte> nonce, ulong sequence)
        {
            nonce.Slice(0, 4).Clear();
            BinaryPrimitives.WriteUInt64LittleEndian(nonce.Slice(4), sequence);
        }
    }
}
