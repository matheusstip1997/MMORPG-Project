namespace Shared.Simulation.Stats
{
    /// <summary>
    /// Adventurer rank, F (lowest) to SS (spec 3.6 R-3.6-01). Monsters have ranks too. The numeric value is the index
    /// into per-rank config arrays (<c>DamageConstants.MitigationConstantByRank</c>); in config the names are camelCase
    /// (<c>f</c> ... <c>ss</c>).
    /// </summary>
    public enum Rank : byte
    {
        F = 0,
        E = 1,
        D = 2,
        C = 3,
        B = 4,
        A = 5,
        S = 6,
        SS = 7,
    }

    public static class Ranks
    {
        public const int Count = 8;
    }
}
