using System;
using System.Buffers.Binary;

namespace Shared.Security
{
    /// <summary>
    /// Poly1305 one-time authenticator, RFC 8439 section 2.5, in the 26-bit-limb form of poly1305-donna: no branches or
    /// table lookups on secret data. The key must never be used for two messages; the AEAD derives a fresh one per nonce.
    /// </summary>
    public struct Poly1305
    {
        public const int KeySize = 32;
        public const int TagSize = 16;
        public const int BlockSize = 16;

        private const uint Mask26 = 0x3ffffff;

        private uint _r0, _r1, _r2, _r3, _r4;
        private uint _s1, _s2, _s3, _s4;
        private uint _h0, _h1, _h2, _h3, _h4;
        private uint _pad0, _pad1, _pad2, _pad3;

        /// <summary>One-shot MAC of <paramref name="message"/> (any length).</summary>
        public static void ComputeTag(ReadOnlySpan<byte> key, ReadOnlySpan<byte> message, Span<byte> tag)
        {
            var mac = new Poly1305(key);
            var full = message.Length - message.Length % BlockSize;
            mac.Blocks(message.Slice(0, full), fullBlocks: true);
            if (full < message.Length)
            {
                // A final partial block is padded with a single 1 byte, then zeros, and gets no high bit.
                Span<byte> last = stackalloc byte[BlockSize];
                last.Clear();
                message.Slice(full).CopyTo(last);
                last[message.Length - full] = 1;
                mac.Blocks(last, fullBlocks: false);
            }
            mac.Finish(tag);
        }

        public Poly1305(ReadOnlySpan<byte> key)
        {
            if (key.Length != KeySize)
                throw new ArgumentException($"Key must be {KeySize} bytes.", nameof(key));

            // r is clamped as the RFC requires.
            _r0 = BinaryPrimitives.ReadUInt32LittleEndian(key) & 0x3ffffff;
            _r1 = (BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(3)) >> 2) & 0x3ffff03;
            _r2 = (BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(6)) >> 4) & 0x3ffc0ff;
            _r3 = (BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(9)) >> 6) & 0x3f03fff;
            _r4 = (BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(12)) >> 8) & 0x00fffff;
            _s1 = _r1 * 5;
            _s2 = _r2 * 5;
            _s3 = _r3 * 5;
            _s4 = _r4 * 5;
            _h0 = _h1 = _h2 = _h3 = _h4 = 0;
            _pad0 = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(16));
            _pad1 = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(20));
            _pad2 = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(24));
            _pad3 = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(28));
        }

        /// <summary>
        /// Absorbs <paramref name="data"/> zero-padded to a multiple of 16 bytes, as the AEAD construction does with the
        /// associated data and the ciphertext (RFC 8439 section 2.8). The padding bytes count as message bytes.
        /// </summary>
        public void UpdatePadded(ReadOnlySpan<byte> data)
        {
            var full = data.Length - data.Length % BlockSize;
            Blocks(data.Slice(0, full), fullBlocks: true);
            if (full == data.Length)
                return;
            Span<byte> last = stackalloc byte[BlockSize];
            last.Clear();
            data.Slice(full).CopyTo(last);
            Blocks(last, fullBlocks: true);
        }

        /// <summary>Writes the tag and clears the state.</summary>
        public void Finish(Span<byte> tag)
        {
            if (tag.Length < TagSize)
                throw new ArgumentException($"Tag must hold {TagSize} bytes.", nameof(tag));

            uint h0 = _h0, h1 = _h1, h2 = _h2, h3 = _h3, h4 = _h4;

            // Full carry.
            uint c = h1 >> 26; h1 &= Mask26;
            h2 += c; c = h2 >> 26; h2 &= Mask26;
            h3 += c; c = h3 >> 26; h3 &= Mask26;
            h4 += c; c = h4 >> 26; h4 &= Mask26;
            h0 += c * 5; c = h0 >> 26; h0 &= Mask26;
            h1 += c;

            // g = h + 5 - 2^130; pick g when it did not underflow, without branching.
            uint g0 = h0 + 5; c = g0 >> 26; g0 &= Mask26;
            uint g1 = h1 + c; c = g1 >> 26; g1 &= Mask26;
            uint g2 = h2 + c; c = g2 >> 26; g2 &= Mask26;
            uint g3 = h3 + c; c = g3 >> 26; g3 &= Mask26;
            uint g4 = h4 + c - (1u << 26);

            uint select = (g4 >> 31) - 1;
            g0 &= select; g1 &= select; g2 &= select; g3 &= select; g4 &= select;
            select = ~select;
            h0 = (h0 & select) | g0;
            h1 = (h1 & select) | g1;
            h2 = (h2 & select) | g2;
            h3 = (h3 & select) | g3;
            h4 = (h4 & select) | g4;

            // h mod 2^128, then + pad.
            h0 = h0 | (h1 << 26);
            h1 = (h1 >> 6) | (h2 << 20);
            h2 = (h2 >> 12) | (h3 << 14);
            h3 = (h3 >> 18) | (h4 << 8);

            ulong f = (ulong)h0 + _pad0; h0 = (uint)f;
            f = (ulong)h1 + _pad1 + (f >> 32); h1 = (uint)f;
            f = (ulong)h2 + _pad2 + (f >> 32); h2 = (uint)f;
            f = (ulong)h3 + _pad3 + (f >> 32); h3 = (uint)f;

            BinaryPrimitives.WriteUInt32LittleEndian(tag, h0);
            BinaryPrimitives.WriteUInt32LittleEndian(tag.Slice(4), h1);
            BinaryPrimitives.WriteUInt32LittleEndian(tag.Slice(8), h2);
            BinaryPrimitives.WriteUInt32LittleEndian(tag.Slice(12), h3);

            this = default;
        }

        /// <param name="fullBlocks">True for 16-byte message blocks (adds 2^128); false for an already padded last block.</param>
        private void Blocks(ReadOnlySpan<byte> data, bool fullBlocks)
        {
            uint hibit = fullBlocks ? 1u << 24 : 0;
            uint r0 = _r0, r1 = _r1, r2 = _r2, r3 = _r3, r4 = _r4;
            uint s1 = _s1, s2 = _s2, s3 = _s3, s4 = _s4;
            uint h0 = _h0, h1 = _h1, h2 = _h2, h3 = _h3, h4 = _h4;

            for (var offset = 0; offset + BlockSize <= data.Length; offset += BlockSize)
            {
                var m = data.Slice(offset, BlockSize);
                h0 += BinaryPrimitives.ReadUInt32LittleEndian(m) & Mask26;
                h1 += (BinaryPrimitives.ReadUInt32LittleEndian(m.Slice(3)) >> 2) & Mask26;
                h2 += (BinaryPrimitives.ReadUInt32LittleEndian(m.Slice(6)) >> 4) & Mask26;
                h3 += (BinaryPrimitives.ReadUInt32LittleEndian(m.Slice(9)) >> 6) & Mask26;
                h4 += (BinaryPrimitives.ReadUInt32LittleEndian(m.Slice(12)) >> 8) | hibit;

                ulong d0 = (ulong)h0 * r0 + (ulong)h1 * s4 + (ulong)h2 * s3 + (ulong)h3 * s2 + (ulong)h4 * s1;
                ulong d1 = (ulong)h0 * r1 + (ulong)h1 * r0 + (ulong)h2 * s4 + (ulong)h3 * s3 + (ulong)h4 * s2;
                ulong d2 = (ulong)h0 * r2 + (ulong)h1 * r1 + (ulong)h2 * r0 + (ulong)h3 * s4 + (ulong)h4 * s3;
                ulong d3 = (ulong)h0 * r3 + (ulong)h1 * r2 + (ulong)h2 * r1 + (ulong)h3 * r0 + (ulong)h4 * s4;
                ulong d4 = (ulong)h0 * r4 + (ulong)h1 * r3 + (ulong)h2 * r2 + (ulong)h3 * r1 + (ulong)h4 * r0;

                ulong carry = d0 >> 26; h0 = (uint)d0 & Mask26;
                d1 += carry; carry = d1 >> 26; h1 = (uint)d1 & Mask26;
                d2 += carry; carry = d2 >> 26; h2 = (uint)d2 & Mask26;
                d3 += carry; carry = d3 >> 26; h3 = (uint)d3 & Mask26;
                d4 += carry; carry = d4 >> 26; h4 = (uint)d4 & Mask26;
                h0 += (uint)carry * 5;
                uint c = h0 >> 26; h0 &= Mask26;
                h1 += c;
            }

            _h0 = h0; _h1 = h1; _h2 = h2; _h3 = h3; _h4 = h4;
        }
    }
}
