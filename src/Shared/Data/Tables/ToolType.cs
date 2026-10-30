namespace Shared.Data.Tables
{
    /// <summary>
    /// Gathering tool types (R-5.2-02: pickaxe for minerals, axe for wood, fishing rod). Config enum (camelCase names):
    /// append only, never renumber. M3 uses the pickaxe; the axe and the rod wait for their jobs.
    /// </summary>
    public enum ToolType : byte
    {
        None = 0,
        Pickaxe = 1,
        Axe = 2,
        Rod = 3,
    }
}
