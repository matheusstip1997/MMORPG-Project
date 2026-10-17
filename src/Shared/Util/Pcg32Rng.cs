using System;

namespace Shared.Util
{
    /// <summary>
    /// PCG32 (PCG-XSH-RR, 64-bit state, 32-bit output), M. E. O'Neill, "PCG: A Family of Simple Fast
    /// Space-Efficient Statistically Good Algorithms for Random Number Generation", 2014; reference code
    /// at https://www.pcg-random.org. Seeding and bounded draws follow the reference
    /// <c>pcg32_srandom_r</c> and <c>pcg32_boundedrand_r</c> exactly, so the output matches the published
    /// test vectors.
    /// <para>
    /// Only unsigned 64-bit multiply, add, shift and rotate are used (all wrap-around, no floating point),
    /// so the sequence is bit-identical on Mono, IL2CPP and .NET (architecture rule 6).
    /// </para>
    /// <para>
    /// Not thread-safe and not cryptographic. Log <see cref="Seed"/> and <see cref="Stream"/> when
    /// creating a generator; together they reproduce the whole sequence.
    /// </para>
    /// </summary>
    public sealed class Pcg32Rng : IRng
    {
        private const ulong Multiplier = 6364136223846793005UL;

        private ulong _state;
        private readonly ulong _increment;

        /// <param name="seed">Starting state; log it.</param>
        /// <param name="stream">Selects one of 2^63 independent sequences; log it too.</param>
        public Pcg32Rng(ulong seed, ulong stream = 0)
        {
            Seed = seed;
            Stream = stream;
            _state = 0;
            _increment = (stream << 1) | 1UL;
            NextUInt();
            _state += seed;
            NextUInt();
        }

        public ulong Seed { get; }
        public ulong Stream { get; }

        public uint NextUInt()
        {
            ulong old = _state;
            _state = unchecked(old * Multiplier + _increment);
            uint xorShifted = (uint)(((old >> 18) ^ old) >> 27);
            int rotation = (int)(old >> 59);
            return (xorShifted >> rotation) | (xorShifted << ((-rotation) & 31));
        }

        public int NextInt(int maxExclusive)
        {
            if (maxExclusive <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxExclusive), "must be positive");
            return (int)NextBounded((uint)maxExclusive);
        }

        public int NextInt(int minInclusive, int maxExclusive)
        {
            if (maxExclusive <= minInclusive)
                throw new ArgumentOutOfRangeException(nameof(maxExclusive), "range is empty");
            uint range = (uint)((long)maxExclusive - minInclusive);
            return (int)(minInclusive + (long)NextBounded(range));
        }

        public bool RollPercent(int percent)
        {
            return NextInt(100) < percent;
        }

        // Rejection sampling from the reference pcg32_boundedrand_r: unbiased for every bound.
        private uint NextBounded(uint bound)
        {
            uint threshold = unchecked((uint)-bound) % bound;
            while (true)
            {
                uint r = NextUInt();
                if (r >= threshold)
                    return r % bound;
            }
        }
    }
}
