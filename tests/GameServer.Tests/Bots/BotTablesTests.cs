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
        // WP-M2-12, spec 12.2: the M2 exit run plays the loop to level 30; the loop's steps are every known step.
        var exit = scenarios["m2-exit"];
        Assert.Equal((5, 30, "m2-loop"), (exit.Bots, exit.TargetLevel, exit.BehaviourId));
        Assert.Equal(0, scenarios["ci-smoke"].TargetLevel);
        Assert.True(result.Set.TryGet(typeof(BotBehaviour), out var behaviours));
        var loop = behaviours.Rows.Cast<BotBehaviour>().Single(b => b.Id == "m2-loop");
        Assert.Equal(BotBehaviour.LoopStepIds, loop.LoopSteps);
        Assert.Empty(behaviours.Rows.Cast<BotBehaviour>().Single(b => b.Id == "field-combat").LoopSteps);
    }

    [Fact]
    public void Loop_steps_must_be_known_once_each_and_include_the_fight()
    {
        var behaviour = new BotBehaviour
        {
            Id = "x", HomeMarkerIds = ["m1.spawn.combat.a"], LoopSteps = ["pickup", "dance", "pickup"],
        };
        var errors = new RowErrors();
        behaviour.Validate(errors);
        Assert.Equal(3, errors.Items.Count(e => e.Field == "LoopSteps")); // unknown, twice, no fight

        var fine = new BotBehaviour { Id = "y", HomeMarkerIds = ["m1.spawn.combat.a"], LoopSteps = ["fight", "sell"] };
        errors = new RowErrors();
        fine.Validate(errors);
        Assert.Empty(errors.Items);
    }
}
