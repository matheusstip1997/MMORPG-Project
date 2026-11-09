using EconomySimulator;
using GameServer.Tests.Support;
using Shared.Data;
using Shared.Data.Tables;

namespace GameServer.Tests.Tools;

/// <summary>
/// The economy simulator's fee volume (spec 12.5, 4.2 R-4.2-04/05; WP-M4-1): an M4 row's listings, purchases and contracts pay
/// the <c>FeeRates</c> fees as sinks, the sink cover line counts them, a fee type that never moves fails "fee sinks reached",
/// and a row without fee volume gives the numbers it always gave (the fee rolls have their own stream).
/// </summary>
public class EconomySimulatorFeeTests
{
    private static SimEconomy Economy(ConfigSet? tables = null) => SimEconomy.From(tables ?? TestCombat.Tables(), RepositoryPaths.M1World());

    private static EconomySimScenario Row(SimEconomy economy, string id, bool fees, int hours = 2)
    {
        var source = economy.Scenarios.Single(s => s.Id == id);
        return new EconomySimScenario
        {
            Id = source.Id, Players = source.Players, Hours = hours, Runs = 1, Seed = source.Seed,
            KillsPerPlayerMinuteMilli = source.KillsPerPlayerMinuteMilli, MonsterIds = source.MonsterIds, KillsPerVisit = source.KillsPerVisit,
            BuyItemIds = source.BuyItemIds, BuyKeep = source.BuyKeep, UsedPerPlayerHour = source.UsedPerPlayerHour,
            SampleMinutes = source.SampleMinutes, SettleWindowPercent = source.SettleWindowPercent,
            SettleTolerancePercent = source.SettleTolerancePercent,
            ListingsPerPlayerHour = fees ? source.ListingsPerPlayerHour : 0, TradesPerPlayerHour = fees ? source.TradesPerPlayerHour : 0,
            ContractsPerPlayerHour = fees ? source.ContractsPerPlayerHour : 0, TradeValueMinGold = source.TradeValueMinGold,
            TradeValueMaxGold = source.TradeValueMaxGold,
        };
    }

    [Fact]
    public void An_m4_row_destroys_every_fee_type_and_the_sink_cover_counts_the_fees()
    {
        var economy = Economy();

        var result = SimRunner.Play(economy, Row(economy, "m4-15", fees: true));

        Assert.Equal(FeeNames.All, result.Fees.Select(f => f.Name));
        Assert.All(result.Fees, f => Assert.True(f.Total > 0, f.Name));
        Assert.Contains(result.Verdicts, v => v.Name == "fee sinks reached" && v.Pass);
        var cover = Assert.Single(result.Verdicts, v => v.Name == "sink cover");
        Assert.Contains("vendor sinks and fees destroy", cover.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("(fees 0%", cover.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void The_fee_volume_never_changes_the_hunt_the_sales_or_the_vendors()
    {
        var economy = Economy();

        var with = SimRunner.Play(economy, Row(economy, "m4-15", fees: true));
        var without = SimRunner.Play(economy, Row(economy, "m4-15", fees: false));

        Assert.Empty(without.Fees);
        Assert.DoesNotContain(without.Verdicts, v => v.Name == "fee sinks reached");
        Assert.Equal(without.HourTotals.Select(h => h.Kills), with.HourTotals.Select(h => h.Kills));
        Assert.Equal(without.HourTotals.Select(h => h.GoldCreated), with.HourTotals.Select(h => h.GoldCreated));
    }

    [Fact]
    public void A_fee_rate_of_zero_fails_fee_sinks_reached()
    {
        var tables = TestCombat.Tables();
        tables.Add(new ConfigTable(typeof(FeeRates), 1, new object[]
        {
            new FeeRates { Id = "listing", DefaultPercent = 0, MinPercent = 0, MaxPercent = 10, DestroyedSharePercent = 50 },
            new FeeRates { Id = "saleTax", DefaultPercent = 5, MinPercent = 0, MaxPercent = 15, DestroyedSharePercent = 50 },
            new FeeRates { Id = "contract", DefaultPercent = 5, MinPercent = 0, MaxPercent = 15, DestroyedSharePercent = 50 },
        }));
        var economy = Economy(tables);

        var result = SimRunner.Play(economy, Row(economy, "m4-15", fees: true, hours: 1));

        var verdict = Assert.Single(result.Verdicts, v => v.Name == "fee sinks reached");
        Assert.False(verdict.Pass);
        Assert.Contains("listing fees", verdict.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_row_with_fee_volume_needs_every_fee_rate()
    {
        var tables = TestCombat.Tables();
        tables.Add(new ConfigTable(typeof(FeeRates), 1, Array.Empty<object>()));
        var economy = Economy(tables);

        var error = Assert.Throws<InvalidDataException>(() => new Simulation(economy, Row(economy, "m4-15", fees: true)));
        Assert.Contains("FeeRates", error.Message, StringComparison.Ordinal);
    }
}
