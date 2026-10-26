namespace Shared.Data.Tables
{
    /// <summary>
    /// Item types of R-4.1-01: sell-only drops, craft materials, equipment, consumables, molds and tools, quest items.
    /// Config enum (camelCase names) and stored in telemetry: append only, never renumber.
    /// </summary>
    public enum ItemCategory : byte
    {
        SellOnly = 0,
        Material = 1,
        Equipment = 2,
        Consumable = 3,
        MoldOrTool = 4,
        Quest = 5,
    }

    public static class ItemCategories
    {
        /// <summary>
        /// Spec 4.1 implementation: stackables are rows of <c>(defId, rarity, stolenFlag) x count</c>; equipment, molds and
        /// tools are instances (M2+). M1 consumables have no stat profile, so they stack by definition (crafted ones, with a
        /// stat profile in the stack key, come in M3). Quest items are bound (M2+).
        /// </summary>
        public static bool IsStackable(ItemCategory category) =>
            category == ItemCategory.SellOnly || category == ItemCategory.Material || category == ItemCategory.Consumable;
    }
}
