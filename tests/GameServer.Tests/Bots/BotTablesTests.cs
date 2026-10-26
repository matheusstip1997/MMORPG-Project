using GameServer.Tests.Support;
using Shared.Data;
using Shared.Data.Tables;

namespace GameServer.Tests.Bots;

/// <summary>Backlog J: the cross-table rules of the bot tables (DataValidator runs the same).</summary>
public class BotTablesTests
{
    [Fact]
    public void A_scenario_must_fit_the_server_limits_and_the_allowed_classes()
    {
        var set = new ConfigSet();
        set.Add(new ConfigTable(typeof(NetSecurity), 2, [new NetSecurity { MaxPeers = 40, ConnectRequestsPerSecond = 10 }]));
        set.Add(new ConfigTable(typeof(CharacterCreationConfig), 1,
            [new CharacterCreationConfig { AllowedInitialClasses = ["warrior", "archer"] }]));
        var scenario = new BotScenario { Id = "x", Bots = 50, RampPerSecond = 20, Classes = ["warrior", "mage"] };

        var errors = new RowErrors();
        scenario.Validate(set, errors);

        Assert.Equal(["Bots", "RampPerSecond", "Classes"], errors.Items.Select(e => e.Field));
    }

    [Fact]
    public void The_repository_scenarios_are_valid_and_name_the_ci_and_exit_runs()
    {
        var result = GameServer.Host.Config.ConfigLoader.LoadAndValidate(RepositoryPaths.Tables(), ConfigTables.All);
        Assert.True(result.Ok, string.Join("\n", result.Errors));
        Assert.True(result.Set.TryGet(typeof(BotScenario), out var table));
        var scenarios = table.Rows.Cast<BotScenario>().ToDictionary(s => s.Id);
        // Spec 11.5 / user decision 2026-10-26: about 20 bots in CI, 2 minutes on pushes, 5 on main; spec 12.4: 50 bots.
        Assert.Equal((20, 120_000, 50), (scenarios["ci-smoke"].Bots, scenarios["ci-smoke"].DurationMs, scenarios["ci-smoke"].TickP99MaxPercentOfPeriod));
        Assert.Equal((20, 300_000), (scenarios["ci-merge"].Bots, scenarios["ci-merge"].DurationMs));
        Assert.Equal((50, 100), (scenarios["m1-exit"].Bots, scenarios["m1-exit"].TickP99MaxPercentOfPeriod));
    }
}
