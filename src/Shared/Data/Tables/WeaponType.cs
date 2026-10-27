namespace Shared.Data.Tables
{
    /// <summary>
    /// Weapon types (spec 4.1 M2 item model, R-3.5-07; the list of <c>docs/design/skill-trees.json</c> <c>weaponTypes</c>).
    /// Config enum (camelCase names): append only, never renumber. <see cref="None"/> is "not a weapon".
    /// </summary>
    public enum WeaponType : byte
    {
        None = 0,
        Sword = 1,
        Shield = 2,
        Rapier = 3,
        DualSwords = 4,
        BigSword = 5,
        BigAxe = 6,
        Spear = 7,
        Bow = 8,
        DualCrossbows = 9,
        Dagger = 10,
        DualDaggers = 11,
        Scythe = 12,
        Pistol = 13,
        DualPistols = 14,
        Staff = 15,
        Wand = 16,
        Book = 17,
        Harp = 18,
        Gloves = 19,
        Vajra = 20,
        Stick = 21,
    }

    public static class WeaponTypes
    {
        /// <summary>
        /// R-3.5-07 (user decision 2026-10-27): the dual types are weapon types of their own, and a dual weapon is one item
        /// that fills both weapon slots.
        /// </summary>
        public static bool IsDual(WeaponType type) =>
            type == WeaponType.DualDaggers || type == WeaponType.DualSwords || type == WeaponType.DualPistols
            || type == WeaponType.DualCrossbows;
    }
}
