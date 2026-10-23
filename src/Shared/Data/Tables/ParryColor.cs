namespace Shared.Data.Tables
{
    /// <summary>
    /// Attack colors (R-3.3-04): red cannot be parried, yellow only by heavy armor, blue by medium or heavy armor, green by
    /// anyone; <see cref="None"/> is an uncolored attack, which a parry reduces. Config enum (camelCase names), sent in
    /// snapshots (<c>Snapshot.ParryColorBits</c>) and stored in telemetry: append only, never renumber.
    /// </summary>
    public enum ParryColor : byte
    {
        None = 0,
        Red = 1,
        Yellow = 2,
        Blue = 3,
        Green = 4,
    }
}
