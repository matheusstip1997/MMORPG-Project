namespace Shared.Data.Tables
{
    /// <summary>
    /// Armor class of a character (R-3.3-05): set by the class (<see cref="ClassDef.ArmorClass"/>, R-3.5-08), not by
    /// equipment. It decides which parry colors a character can parry (R-3.3-04). Config enum (camelCase names); append
    /// only, never renumber.
    /// </summary>
    public enum ArmorClass : byte
    {
        Light = 1,
        Medium = 2,
        Heavy = 3,
    }
}
