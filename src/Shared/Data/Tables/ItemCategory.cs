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
        /// tools are instances (<see cref="IsInstance"/>, M2). Consumables have no stat profile yet, so they stack by
        /// definition (crafted ones, with a stat profile in the stack key, come in M3). Quest items stack; they are bound
        /// (<see cref="IsNonTradable"/>).
        /// </summary>
        public static bool IsStackable(ItemCategory category) =>
            category == ItemCategory.SellOnly || category == ItemCategory.Material || category == ItemCategory.Consumable
            || category == ItemCategory.Quest;

        /// <summary>One item per row with its own id (spec 4.1 M2 item model): equipment, molds and tools. Max stack 1.</summary>
        public static bool IsInstance(ItemCategory category) =>
            category == ItemCategory.Equipment || category == ItemCategory.MoldOrTool;

        /// <summary>
        /// R-4.1-01: sell-only drops and quest items are non-tradable by their type. Carried, inert until trade exists (M4,
        /// R-4.1-04); an instance also has its own non-tradable flag.
        /// </summary>
        public static bool IsNonTradable(ItemCategory category) =>
            category == ItemCategory.SellOnly || category == ItemCategory.Quest;
    }
}
