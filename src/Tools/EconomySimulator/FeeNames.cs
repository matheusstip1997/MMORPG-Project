using Shared.Simulation.Economy;

namespace EconomySimulator;

/// <summary>The fee lines of a scenario's report (spec 12.5, 4.2 R-4.2-04/05; WP-M4-1): gold each fee type destroyed.</summary>
public static class FeeNames
{
    public const string ListingFees = "listing fees";
    public const string SaleTaxes = "sale taxes";
    public const string ContractFees = "contract fees";

    /// <summary>Every fee line, in <see cref="FeeType"/> order.</summary>
    public static readonly IReadOnlyList<string> All = [ListingFees, SaleTaxes, ContractFees];

    public static string Of(FeeType type) => All[(int)type];
}
