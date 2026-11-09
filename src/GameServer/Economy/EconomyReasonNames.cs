using GameServer.Events;

namespace GameServer.Economy;

/// <summary>
/// The names economy figures carry for an <see cref="InventoryReason"/> (WP-M4-5): the member name in camelCase, as the
/// BotClient economy report prints them (<c>EconomyReport.InventoryReasonNames</c>; a test compares the two) and as the
/// snapshot rows and the admin panel's economy view store and show them. <c>gold_ledger.reason</c> holds the member name.
/// </summary>
public static class EconomyReasonNames
{
    /// <summary>Every reason but <see cref="InventoryReason.None"/>, in value order: a reason appended to the enum appears here.</summary>
    public static IReadOnlyList<string> All { get; } = Enum.GetValues(typeof(InventoryReason)).Cast<InventoryReason>()
        .Where(r => r != InventoryReason.None).OrderBy(r => (int)r).Select(Of).ToArray();

    public static string Of(InventoryReason reason) => CamelCase(reason.ToString());

    /// <summary>The name of a <c>gold_ledger.reason</c> (the member name); text no member has is kept, in camelCase.</summary>
    public static string OfLedger(string memberName) => CamelCase(memberName);

    private static string CamelCase(string name) =>
        name.Length == 0 || char.IsLower(name[0]) ? name : char.ToLowerInvariant(name[0]) + name.Substring(1);
}
