using System;
using System.Buffers.Binary;

namespace Shared.Protocol
{
    /// <summary>
    /// Checks the layout of a MemoryPack body before MemoryPack reads it (R-2.5-04, R-2.5-07). MemoryPack trusts its
    /// string headers: a UTF-8 header claiming <c>int.MaxValue</c> bytes overflows its bounds check and makes it read past
    /// the buffer (an access violation, which no catch can stop; reproduced by <c>MemoryPackCrashReproductionTests</c>).
    /// So no reader calls MemoryPack directly: <see cref="ReliableMessageReader"/> walks every body against its
    /// <see cref="MemoryPackLayout"/> here first and deserializes only a body that passed and was consumed exactly.
    /// <para>
    /// Layouts (MemoryPack 1.21, UTF-8 strings): an object is one member-count byte, then its members; a string is an int 0
    /// (empty) or an int <c>~byteCount</c>, an int UTF-16 length and the UTF-8 bytes; an array is an int count, then its
    /// elements. Null objects, strings and arrays, and UTF-16 strings, are refused: our writers never produce them.
    /// </para>
    /// <para>
    /// Bounded in time and allocation free: every step consumes at least one byte of the body, so a walk is linear in its
    /// size (which the reader caps first), and nothing here allocates or throws.
    /// </para>
    /// </summary>
    public static class MemoryPackGuard
    {
        private const int StringHeaderBytes = 8;
        private const int ArrayHeaderBytes = 4;

        /// <summary>True when <paramref name="body"/> is exactly one value of <paramref name="layout"/>, nothing more.</summary>
        public static bool Matches(ReadOnlySpan<byte> body, MemoryPackLayout layout)
        {
            if (layout == null)
                throw new ArgumentNullException(nameof(layout));
            return Walk(ref body, layout) && body.IsEmpty;
        }

        /// <summary>The object header: exactly <paramref name="members"/> members follow.</summary>
        public static bool Object(ref ReadOnlySpan<byte> body, int members)
        {
            if (body.Length < 1 || body[0] != members)
                return false;
            body = body.Slice(1);
            return true;
        }

        /// <summary>A fixed-size member (an unmanaged value such as an int or a byte enum).</summary>
        public static bool Fixed(ref ReadOnlySpan<byte> body, int size)
        {
            if (body.Length < size)
                return false;
            body = body.Slice(size);
            return true;
        }

        /// <summary>A bool: one byte, 0 or 1 (MemoryPack would read any other byte as a bool that is neither).</summary>
        public static bool Bool(ref ReadOnlySpan<byte> body)
        {
            if (body.Length < 1 || body[0] > 1)
                return false;
            body = body.Slice(1);
            return true;
        }

        /// <summary>
        /// A UTF-8 string of at most <paramref name="maxBytes"/> bytes: the byte count fits the body, the bytes are strict
        /// UTF-8 (no overlong forms, surrogates or values above U+10FFFF), and the UTF-16 length matches them.
        /// </summary>
        public static bool String(ref ReadOnlySpan<byte> body, int maxBytes)
        {
            if (body.Length < 4)
                return false;
            int header = BinaryPrimitives.ReadInt32LittleEndian(body);
            if (header == 0)
            {
                body = body.Slice(4);
                return true;
            }
            // -1 is null and a positive header is a UTF-16 string.
            if (header > -2 || body.Length < StringHeaderBytes)
                return false;
            int byteCount = ~header;
            if (byteCount > maxBytes || byteCount > body.Length - StringHeaderBytes)
                return false;
            int utf16Length = BinaryPrimitives.ReadInt32LittleEndian(body.Slice(4));
            if (!TryCountUtf16(body.Slice(StringHeaderBytes, byteCount), out int counted) || counted != utf16Length)
                return false;
            body = body.Slice(StringHeaderBytes + byteCount);
            return true;
        }

        /// <summary>
        /// An array of at most <paramref name="maxCount"/> elements of <paramref name="element"/>. A count the rest of the
        /// body cannot hold (every element takes at least one byte) is refused before any element is walked.
        /// </summary>
        public static bool Array(ref ReadOnlySpan<byte> body, MemoryPackLayout element, int maxCount)
        {
            if (body.Length < ArrayHeaderBytes)
                return false;
            int count = BinaryPrimitives.ReadInt32LittleEndian(body);
            body = body.Slice(ArrayHeaderBytes);
            if (count < 0 || count > maxCount || count > body.Length)
                return false;
            for (int i = 0; i < count; i++)
            {
                if (!Walk(ref body, element))
                    return false;
            }
            return true;
        }

        private static bool Walk(ref ReadOnlySpan<byte> body, MemoryPackLayout layout)
        {
            switch (layout.Kind)
            {
                case MemoryPackLayout.NodeKind.Object:
                    if (!Object(ref body, layout.Members.Length))
                        return false;
                    foreach (var member in layout.Members)
                    {
                        if (!Walk(ref body, member))
                            return false;
                    }
                    return true;
                case MemoryPackLayout.NodeKind.Fixed:
                    return Fixed(ref body, layout.Size);
                case MemoryPackLayout.NodeKind.Bool:
                    return Bool(ref body);
                case MemoryPackLayout.NodeKind.String:
                    return String(ref body, layout.Size);
                case MemoryPackLayout.NodeKind.Array:
                    return Array(ref body, layout.Members[0], layout.Size);
                default:
                    return false;
            }
        }

        /// <summary>
        /// Strict UTF-8 (RFC 3629): the UTF-16 length of <paramref name="utf8"/>, or false for any invalid sequence. Our
        /// own loop rather than a strict <c>UTF8Encoding</c>, which throws (and allocates) on bad input.
        /// </summary>
        private static bool TryCountUtf16(ReadOnlySpan<byte> utf8, out int utf16Length)
        {
            utf16Length = 0;
            int i = 0;
            while (i < utf8.Length)
            {
                int lead = utf8[i];
                if (lead < 0x80)
                {
                    i++;
                    utf16Length++;
                    continue;
                }
                int trail;
                int codePoint;
                if (lead >= 0xC2 && lead <= 0xDF)
                {
                    trail = 1;
                    codePoint = lead & 0x1F;
                }
                else if (lead >= 0xE0 && lead <= 0xEF)
                {
                    trail = 2;
                    codePoint = lead & 0x0F;
                }
                else if (lead >= 0xF0 && lead <= 0xF4)
                {
                    trail = 3;
                    codePoint = lead & 0x07;
                }
                else
                {
                    return false; // a continuation byte, an overlong 2-byte lead (C0, C1) or above U+10FFFF (F5..FF)
                }
                if (utf8.Length - i <= trail)
                    return false;
                for (int k = 1; k <= trail; k++)
                {
                    int next = utf8[i + k];
                    if ((next & 0xC0) != 0x80)
                        return false;
                    codePoint = (codePoint << 6) | (next & 0x3F);
                }
                if (trail == 2 && (codePoint < 0x800 || (codePoint >= 0xD800 && codePoint <= 0xDFFF)))
                    return false;
                if (trail == 3 && (codePoint < 0x10000 || codePoint > 0x10FFFF))
                    return false;
                utf16Length += trail == 3 ? 2 : 1;
                i += trail + 1;
            }
            return true;
        }
    }
}
