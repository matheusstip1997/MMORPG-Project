using System;
using System.Buffers.Binary;
using System.Text;

namespace Shared.Protocol
{
    /// <summary>
    /// Checks the layout of a MemoryPack body before MemoryPack reads it (R-2.5-04). MemoryPack trusts its string headers:
    /// fuzzing found a mutated UTF-8 header that made it read past the buffer (an access violation, which no catch can
    /// stop). So every reader walks the body with these checks first, member by member in declaration order, and calls
    /// MemoryPack only on a body that passed them and was consumed exactly.
    /// <para>
    /// Layouts (MemoryPack 1.21, UTF-8 strings): an object is one member-count byte, then its members; a string is an int 0
    /// (empty) or an int <c>~byteCount</c>, an int UTF-16 length and the UTF-8 bytes. Null objects and strings, and UTF-16
    /// strings, are refused: our writers never produce them.
    /// </para>
    /// </summary>
    public static class MemoryPackGuard
    {
        private const int StringHeaderBytes = 8;

        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

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

        /// <summary>A UTF-8 string: the byte count fits the body, the bytes are valid UTF-8, and the UTF-16 length matches them.</summary>
        public static bool String(ref ReadOnlySpan<byte> body)
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
            if (byteCount > body.Length - StringHeaderBytes)
                return false;
            int utf16Length = BinaryPrimitives.ReadInt32LittleEndian(body.Slice(4));
            try
            {
                if (StrictUtf8.GetCharCount(body.Slice(StringHeaderBytes, byteCount)) != utf16Length)
                    return false;
            }
            catch (DecoderFallbackException)
            {
                return false;
            }
            body = body.Slice(StringHeaderBytes + byteCount);
            return true;
        }
    }
}
