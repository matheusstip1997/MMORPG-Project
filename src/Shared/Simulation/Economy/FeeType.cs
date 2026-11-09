namespace Shared.Simulation.Economy
{
    /// <summary>
    /// The fee types of <c>FeeService</c> (spec 4.2 R-4.2-05, M4 decision 3; WP-M4-1), one <c>FeeRates</c> row each. Stored in
    /// telemetry and the <c>FeeCharged</c> event by value: append only.
    /// </summary>
    public enum FeeType : byte
    {
        /// <summary>Paid when an auction or stall listing is created, on the asking price; never returned.</summary>
        Listing = 0,

        /// <summary>Paid by the seller at an auction or stall settlement, on the sale price.</summary>
        SaleTax = 1,

        /// <summary>Paid at a contract's completion, on the contract gold (spec 5.4).</summary>
        Contract = 2,
    }

    /// <summary>The <c>FeeRates</c> row ids of the fee types (camelCase, as in the tables).</summary>
    public static class FeeTypes
    {
        /// <summary>Every fee type, in value order.</summary>
        public static readonly FeeType[] All = { FeeType.Listing, FeeType.SaleTax, FeeType.Contract };

        public static string Id(FeeType type)
        {
            switch (type)
            {
                case FeeType.Listing: return "listing";
                case FeeType.SaleTax: return "saleTax";
                case FeeType.Contract: return "contract";
                default: return ((int)type).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        public static bool TryParse(string id, out FeeType type)
        {
            foreach (var candidate in All)
            {
                if (Id(candidate) == id)
                {
                    type = candidate;
                    return true;
                }
            }
            type = default;
            return false;
        }
    }
}
