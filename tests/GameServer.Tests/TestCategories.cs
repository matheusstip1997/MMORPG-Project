namespace GameServer.Tests;

/// <summary>
/// xUnit trait categories CI runs as their own steps (<c>.github/workflows/ci.yml</c>).
/// <para>
/// <see cref="NoDuplication"/> (WP-M4-12; docs/plans/m4-player-economy-plan.md section 1, "No duplication"): the M4
/// concurrency suite. Two buyers take one listing, a listing is bought while cancelled, a trade is confirmed while one item
/// moves or sells, a mail is claimed twice, two players take one loot bag, a contract completes while cancelled, a crash falls
/// between escrow and settlement. After each case, every item and gold over both characters, the escrow and the mailboxes is
/// unchanged, and the <c>gold_ledger</c> flows sum to zero apart from faucet and sink rows (R-4.5-02). Mark such a test with
/// <c>[Trait(TestCategories.Key, TestCategories.NoDuplication)]</c>. CI runs it on every push in the step "No duplication
/// (concurrency suite)" (<c>--filter Category=NoDuplication</c>) and excludes it from the main test step; locally
/// <c>dotnet test Mmorpg.sln</c> runs it with everything else. WP-M4-4 writes the first tests.
/// </para>
/// </summary>
public static class TestCategories
{
    /// <summary>The trait name the <c>--filter Category=...</c> of <c>dotnet test</c> reads.</summary>
    public const string Key = "Category";

    /// <summary>The M4 concurrency suite: value moved between owners is never duplicated or lost.</summary>
    public const string NoDuplication = "NoDuplication";
}
