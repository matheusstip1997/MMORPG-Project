namespace Shared.Util
{
    /// <summary>
    /// The only source of randomness for game code (architecture rule 6: never <c>System.Random</c>).
    /// Implementations must be deterministic for a given seed on every runtime (Mono, IL2CPP, .NET),
    /// so a logged seed reproduces a roll for disputes. Callers log the seed when they create the generator.
    /// </summary>
    public interface IRng
    {
        /// <summary>Next uniformly distributed 32-bit value.</summary>
        uint NextUInt();

        /// <summary>Uniform integer in [0, maxExclusive). <paramref name="maxExclusive"/> must be positive.</summary>
        int NextInt(int maxExclusive);

        /// <summary>Uniform integer in [minInclusive, maxExclusive). The range must not be empty.</summary>
        int NextInt(int minInclusive, int maxExclusive);

        /// <summary>
        /// True with a chance of <paramref name="percent"/> whole percents (config uses whole percents).
        /// Always consumes exactly one draw, so the stream does not depend on the configured chance.
        /// Values at or below 0 never succeed; values at or above 100 always succeed.
        /// </summary>
        bool RollPercent(int percent);
    }
}
