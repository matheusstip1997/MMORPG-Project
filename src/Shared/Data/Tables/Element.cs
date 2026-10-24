namespace Shared.Data.Tables
{
    /// <summary>
    /// The damage elements (R-3.4-07, decided 2026-10-22). Physical and magic are damage kinds, not elements. Each has one
    /// <see cref="ElementCaps"/> row (its resistance stat and damage caps, R-3.8-10). Config enum (camelCase names); append
    /// only, never renumber.
    /// </summary>
    public enum Element : byte
    {
        /// <summary>No element: step 5 of the damage formula is skipped.</summary>
        None = 0,
        Fire = 1,
        Ice = 2,
        Water = 3,
        Wind = 4,
        Earth = 5,
        Lightning = 6,
        Holy = 7,
        Dark = 8,
        Blood = 9,
        Poison = 10,
    }

    public static class Elements
    {
        /// <summary>Highest element value; the elements are 1..Last.</summary>
        public const Element Last = Element.Poison;

        /// <summary>Number of elements, without <see cref="Element.None"/>.</summary>
        public const int Count = (int)Last;

        /// <summary>The element whose camelCase name is <paramref name="id"/> (an <see cref="ElementCaps"/> id).</summary>
        public static bool TryParse(string id, out Element element)
        {
            for (int value = 1; value <= Count; value++)
            {
                if (string.Equals(ConfigTableReader.CamelCase(((Element)value).ToString()), id, System.StringComparison.Ordinal))
                {
                    element = (Element)value;
                    return true;
                }
            }
            element = Element.None;
            return false;
        }
    }
}
