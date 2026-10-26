using System;

namespace Shared.Protocol
{
    /// <summary>
    /// The shape of one MemoryPack value, which <see cref="MemoryPackGuard"/> walks before MemoryPack reads it (R-2.5-07).
    /// An object lists its members in declaration order, so a field appended to a message (spec 11.2) is appended to its
    /// layout in <see cref="ReliableMessageReader"/> too. Every string and array has a bound, so every layout has a
    /// largest encoding (<see cref="MaxBytes"/>). Built once at start; immutable.
    /// </summary>
    public sealed class MemoryPackLayout
    {
        internal enum NodeKind : byte
        {
            Object,
            Fixed,
            Bool,
            String,
            Array,
        }

        private const int ObjectHeaderBytes = 1;
        private const int StringHeaderBytes = 8;
        private const int ArrayHeaderBytes = 4;

        private static readonly MemoryPackLayout[] NoMembers = new MemoryPackLayout[0];

        private MemoryPackLayout(NodeKind kind, int size, MemoryPackLayout[] members, long maxBytes)
        {
            Kind = kind;
            Size = size;
            Members = members;
            if (maxBytes > int.MaxValue)
                throw new ArgumentException("The layout's largest encoding does not fit an int.");
            MaxBytes = (int)maxBytes;
        }

        /// <summary>Fixed: its size in bytes. String: the most UTF-8 bytes. Array: the most elements.</summary>
        internal int Size { get; }

        internal NodeKind Kind { get; }

        /// <summary>Object: the members in order. Array: the element layout.</summary>
        internal MemoryPackLayout[] Members { get; }

        /// <summary>The largest encoding of a value of this layout, in bytes.</summary>
        public int MaxBytes { get; }

        public static readonly MemoryPackLayout U8 = Fixed(1);
        public static readonly MemoryPackLayout U16 = Fixed(2);
        public static readonly MemoryPackLayout U32 = Fixed(4);
        public static readonly MemoryPackLayout Boolean = new MemoryPackLayout(NodeKind.Bool, 1, NoMembers, 1);

        /// <summary>An unmanaged value (an int, a byte enum, ...) of <paramref name="size"/> bytes.</summary>
        public static MemoryPackLayout Fixed(int size)
        {
            if (size < 1)
                throw new ArgumentOutOfRangeException(nameof(size), "must be at least 1");
            return new MemoryPackLayout(NodeKind.Fixed, size, NoMembers, size);
        }

        /// <summary>A non-null UTF-8 string of at most <paramref name="maxBytes"/> bytes.</summary>
        public static MemoryPackLayout String(int maxBytes)
        {
            if (maxBytes < 0)
                throw new ArgumentOutOfRangeException(nameof(maxBytes), "must not be negative");
            return new MemoryPackLayout(NodeKind.String, maxBytes, NoMembers, (long)StringHeaderBytes + maxBytes);
        }

        /// <summary>A non-null array of at most <paramref name="maxCount"/> elements.</summary>
        public static MemoryPackLayout Array(MemoryPackLayout element, int maxCount)
        {
            if (element == null)
                throw new ArgumentNullException(nameof(element));
            if (maxCount < 0)
                throw new ArgumentOutOfRangeException(nameof(maxCount), "must not be negative");
            return new MemoryPackLayout(NodeKind.Array, maxCount, new[] { element },
                ArrayHeaderBytes + (long)maxCount * element.MaxBytes);
        }

        /// <summary>A non-null object with exactly these members, in declaration order.</summary>
        public static MemoryPackLayout Object(params MemoryPackLayout[] members)
        {
            if (members == null)
                throw new ArgumentNullException(nameof(members));
            if (members.Length > 249)
                throw new ArgumentOutOfRangeException(nameof(members), "MemoryPack objects have at most 249 members");
            long maxBytes = ObjectHeaderBytes;
            var copy = new MemoryPackLayout[members.Length];
            for (int i = 0; i < members.Length; i++)
            {
                copy[i] = members[i] ?? throw new ArgumentNullException(nameof(members));
                maxBytes += members[i].MaxBytes;
            }
            return new MemoryPackLayout(NodeKind.Object, members.Length, copy, maxBytes);
        }
    }
}
