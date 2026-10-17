using System;
using System.Buffers.Binary;

namespace Shared.Security
{
    /// <summary>
    /// ChaCha20 stream cipher, RFC 8439 sections 2.3 (block function) and 2.4 (encryption): 256-bit key, 96-bit nonce,
    /// 32-bit block counter. Managed and allocation free, so the Unity client can use it (R-2.5-02). Only add-rotate-xor
    /// on 32-bit words, so the running time does not depend on the key or the data.
    /// </summary>
    public static class ChaCha20
    {
        public const int KeySize = 32;
        public const int NonceSize = 12;
        public const int BlockSize = 64;

        private const int StateWords = 16;

        /// <summary>Writes the 64-byte keystream block for <paramref name="counter"/> into <paramref name="output"/>.</summary>
        public static void Block(ReadOnlySpan<byte> key, uint counter, ReadOnlySpan<byte> nonce, Span<byte> output)
        {
            CheckKeyAndNonce(key, nonce);
            if (output.Length < BlockSize)
                throw new ArgumentException("Output must hold one block.", nameof(output));

            Span<uint> state = stackalloc uint[StateWords];
            Span<uint> working = stackalloc uint[StateWords];
            InitState(state, key, nonce);
            state[12] = counter;
            KeystreamBlock(state, working, output);
        }

        /// <summary>
        /// XORs <paramref name="input"/> with the keystream starting at block <paramref name="initialCounter"/>.
        /// Encrypts and decrypts; <paramref name="output"/> may be the same memory as <paramref name="input"/>.
        /// </summary>
        public static void Xor(ReadOnlySpan<byte> key, uint initialCounter, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> input, Span<byte> output)
        {
            CheckKeyAndNonce(key, nonce);
            if (output.Length < input.Length)
                throw new ArgumentException("Output is smaller than the input.", nameof(output));
            var blocks = ((ulong)input.Length + BlockSize - 1) / BlockSize;
            if (initialCounter + blocks > (ulong)uint.MaxValue + 1)
                throw new ArgumentException("Input is too long for the block counter.", nameof(input));

            Span<uint> state = stackalloc uint[StateWords];
            Span<uint> working = stackalloc uint[StateWords];
            Span<byte> keystream = stackalloc byte[BlockSize];
            InitState(state, key, nonce);
            state[12] = initialCounter;

            var offset = 0;
            while (offset < input.Length)
            {
                KeystreamBlock(state, working, keystream);
                var count = Math.Min(BlockSize, input.Length - offset);
                for (var i = 0; i < count; i++)
                    output[offset + i] = (byte)(input[offset + i] ^ keystream[i]);
                offset += count;
                state[12]++;
            }
            keystream.Clear();
            state.Clear();
            working.Clear();
        }

        private static void CheckKeyAndNonce(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce)
        {
            if (key.Length != KeySize)
                throw new ArgumentException($"Key must be {KeySize} bytes.", nameof(key));
            if (nonce.Length != NonceSize)
                throw new ArgumentException($"Nonce must be {NonceSize} bytes.", nameof(nonce));
        }

        private static void InitState(Span<uint> state, ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce)
        {
            // "expand 32-byte k"
            state[0] = 0x61707865;
            state[1] = 0x3320646e;
            state[2] = 0x79622d32;
            state[3] = 0x6b206574;
            for (var i = 0; i < 8; i++)
                state[4 + i] = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(4 * i));
            for (var i = 0; i < 3; i++)
                state[13 + i] = BinaryPrimitives.ReadUInt32LittleEndian(nonce.Slice(4 * i));
        }

        private static void KeystreamBlock(ReadOnlySpan<uint> state, Span<uint> x, Span<byte> output)
        {
            state.CopyTo(x);
            for (var round = 0; round < 10; round++)
            {
                QuarterRound(x, 0, 4, 8, 12);
                QuarterRound(x, 1, 5, 9, 13);
                QuarterRound(x, 2, 6, 10, 14);
                QuarterRound(x, 3, 7, 11, 15);
                QuarterRound(x, 0, 5, 10, 15);
                QuarterRound(x, 1, 6, 11, 12);
                QuarterRound(x, 2, 7, 8, 13);
                QuarterRound(x, 3, 4, 9, 14);
            }
            for (var i = 0; i < StateWords; i++)
                BinaryPrimitives.WriteUInt32LittleEndian(output.Slice(4 * i), x[i] + state[i]);
        }

        private static void QuarterRound(Span<uint> x, int a, int b, int c, int d)
        {
            x[a] += x[b]; x[d] = RotateLeft(x[d] ^ x[a], 16);
            x[c] += x[d]; x[b] = RotateLeft(x[b] ^ x[c], 12);
            x[a] += x[b]; x[d] = RotateLeft(x[d] ^ x[a], 8);
            x[c] += x[d]; x[b] = RotateLeft(x[b] ^ x[c], 7);
        }

        // BitOperations is not in netstandard2.1.
        private static uint RotateLeft(uint value, int count) => (value << count) | (value >> (32 - count));
    }
}
