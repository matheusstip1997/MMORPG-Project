using System;
using System.Buffers;

namespace Shared.Protocol
{
    /// <summary>
    /// A reusable buffer for one reliable message at a time: the <see cref="ReliableMessageId"/> byte, then the MemoryPack
    /// body (spec 1.7, 11.1). Grows only when a message needs more room than any before it, so steady use allocates
    /// nothing. Not thread safe: one per sending thread.
    /// </summary>
    public sealed class ReliableMessageWriter : IBufferWriter<byte>
    {
        private byte[] _buffer;
        private int _written;

        public ReliableMessageWriter(int initialCapacity = 256)
        {
            if (initialCapacity < 1)
                throw new ArgumentOutOfRangeException(nameof(initialCapacity), "must be at least 1");
            _buffer = new byte[initialCapacity];
        }

        public ReadOnlySpan<byte> Written => new ReadOnlySpan<byte>(_buffer, 0, _written);

        /// <summary>Starts a new message with its id byte.</summary>
        public void Begin(ReliableMessageId id)
        {
            _written = 0;
            Ensure(1);
            _buffer[_written++] = (byte)id;
        }

        public void Advance(int count)
        {
            if (count < 0 || _written + count > _buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(count));
            _written += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            Ensure(sizeHint);
            return new Memory<byte>(_buffer, _written, _buffer.Length - _written);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            Ensure(sizeHint);
            return new Span<byte>(_buffer, _written, _buffer.Length - _written);
        }

        private void Ensure(int sizeHint)
        {
            int needed = Math.Max(sizeHint, 1);
            if (_buffer.Length - _written >= needed)
                return;
            var grown = new byte[Math.Max(_buffer.Length * 2, _written + needed)];
            Buffer.BlockCopy(_buffer, 0, grown, 0, _written);
            _buffer = grown;
        }
    }
}
