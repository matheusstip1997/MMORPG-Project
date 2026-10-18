namespace Shared.Data.Tables
{
    /// <summary>Character slots and soft delete (R-2.1-05, R-9.10-04). Read by the Auth API.</summary>
    [Table("CharacterSlots", 1)]
    [Singleton]
    public sealed class CharacterSlots
    {
        /// <summary>R-2.1-05: 4 slots per account and realm. Characters pending deletion keep their slot.</summary>
        [Range(1, 32)] public int SlotsPerRealm { get; set; }

        /// <summary>
        /// Soft delete grace period: the character (with its name and slot) stays until now + this, then Workers purge it.
        /// TODO(open): the length is undecided (R-2.1-05); the value in data/tables/CharacterSlots.json is a placeholder.
        /// </summary>
        [Range(0, 7_776_000_000)] public long DeleteGraceMs { get; set; }
    }
}
