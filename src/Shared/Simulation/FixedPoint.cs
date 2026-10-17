namespace Shared.Simulation
{
    /// <summary>
    /// Fixed-point format for simulation distances (architecture rule 6: no float in simulation results).
    /// Distances are <c>int</c> centimeters in Q24.8: 256 raw units per centimeter.
    /// <para>
    /// Why sub-centimeter: per-tick steps are rarely whole centimeters (410 cm/s at 20 Hz is 20.5 cm,
    /// a diagonal axis step is about 0.707 of that), and integer cm would drop the fraction every tick.
    /// Q24.8 keeps the error under 1/256 cm per tick, covers +-83 km, and squared distances fit in <c>long</c>.
    /// </para>
    /// </summary>
    public static class FixedPoint
    {
        public const int FractionBits = 8;
        public const int RawPerCm = 1 << FractionBits;

        public static int FromCm(int cm) => cm * RawPerCm;

        /// <summary>Floors toward negative infinity (arithmetic shift).</summary>
        public static int ToCmFloor(int raw) => raw >> FractionBits;

        /// <summary>Rounds to the nearest centimeter; halves round up.</summary>
        public static int ToCmRound(int raw) => (raw + (RawPerCm / 2)) >> FractionBits;
    }
}
