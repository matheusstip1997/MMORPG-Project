#nullable enable
using System;
using Shared.Protocol;

namespace Game.Gameplay.Prediction
{
    /// <summary>
    /// Builds input packets with the newest frame plus the frames before it (spec 1.7 redundancy,
    /// <c>StartingValuesM1.InputRedundancy</c>), so one lost packet is covered by the next. Keeps its own copy of the
    /// last sent frames because acknowledged frames leave the predictor before they leave the redundancy window.
    /// Does not allocate after construction.
    /// </summary>
    public sealed class InputPacketBuilder
    {
        private readonly InputFrame[] _recent;
        private readonly InputFrame[] _newestFirst;
        private int _count;

        public InputPacketBuilder(int redundancy)
        {
            if (redundancy < 1 || redundancy > InputPacket.MaxFrames)
                throw new ArgumentOutOfRangeException(nameof(redundancy));
            _recent = new InputFrame[redundancy];
            _newestFirst = new InputFrame[redundancy];
        }

        /// <summary>Size of the buffer <see cref="Write"/> needs.</summary>
        public int MaxPacketSize => InputPacket.SizeFor(_recent.Length);

        public void Reset() => _count = 0;

        /// <summary>
        /// Adds <paramref name="frame"/> as the newest and writes the packet; returns its length. Frames must come in
        /// sequence order, one per client tick (as <see cref="LocalPlayerPredictor.Predict"/> makes them); an older
        /// frame that does not follow on (after a reset) is left out of the window.
        /// </summary>
        public int Write(in InputFrame frame, Span<byte> destination)
        {
            if (_count > 0)
            {
                ref readonly var newest = ref _recent[(_count - 1) % _recent.Length];
                long tickDelta = (long)frame.ClientTick - newest.ClientTick;
                if (frame.Sequence != (ushort)(newest.Sequence + 1) || tickDelta < 1 || tickDelta > InputPacket.MaxTickDelta)
                    _count = 0;
            }
            _recent[_count % _recent.Length] = frame;
            _count++;

            int frames = Math.Min(_count, _recent.Length);
            for (int i = 0; i < frames; i++)
                _newestFirst[i] = _recent[(_count - 1 - i) % _recent.Length];
            return InputPacket.Write(destination, new ReadOnlySpan<InputFrame>(_newestFirst, 0, frames));
        }
    }
}
