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
        Assert.Equal((5, 30, "m2-exit"), (exit.Bots, exit.TargetLevel, exit.BehaviourId));
        Assert.Equal(0, scenarios["ci-smoke"].TargetLevel);
        Assert.True(result.Set.TryGet(typeof(BotBehaviour), out var behaviours));
        var loop = behaviours.Rows.Cast<BotBehaviour>().Single(b => b.Id == "m2-loop");
        Assert.Equal(BotBehaviour.LoopStepIds, loop.LoopSteps);
        Assert.Empty(behaviours.Rows.Cast<BotBehaviour>().Single(b => b.Id == "field-combat").LoopSteps);
        // Placeholders mirroring the QA kill step (QaTimeouts.ReengageAbovePercent 40, BackOffRecoverSeconds 30).
        Assert.All(behaviours.Rows.Cast<BotBehaviour>().Where(b => b.Id is "field-combat" or "field-combat-hit1" or "m2-loop" or "m2-exit"),
            b => Assert.Equal((40, 30_000), (b.ReengageAbovePercent, b.BackOffRecoverMs)));
    }

    [Fact]
    public void The_exit_run_plays_rounds_of_12_kills_with_planned_back_off_legs_and_the_loop_check_stays_as_it_was()
    {
        var result = GameServer.Host.Config.ConfigLoader.LoadAndValidate(RepositoryPaths.Tables(), ConfigTables.All);
        Assert.True(result.Ok, string.Join("\n", result.Errors));
        Assert.True(result.Set.TryGet(typeof(BotBehaviour), out var table));
        var rows = table.Rows.Cast<BotBehaviour>().ToDictionary(b => b.Id);
        var exit = rows["m2-exit"];
        var loop = rows["m2-loop"];

        // WP-M2-12: 12 kills per outpost visit (the reasoning is in the row), a step limit that holds them, the QA leg margins.
        Assert.Equal((12, 600_000), (exit.LoopKillsPerRound, exit.LoopStepTimeoutMs));
        Assert.Equal((true, 400, 300), (exit.PlanBackOffLegs, exit.BackOffLeashMarginCm, exit.AvoidAggroMarginCm));
        Assert.Equal(BotBehaviour.LoopStepIds, exit.LoopSteps);
        Assert.Equal(loop.HomeMarkerIds, exit.HomeMarkerIds);
        Assert.True(exit.HomesByLevel);
        // The quick every-step check and the before baseline keep one kill per round and straight legs.
        Assert.Equal((1, 120_000, false), (loop.LoopKillsPerRound, loop.LoopStepTimeoutMs, loop.PlanBackOffLegs));
        Assert.All(rows.Values.Where(b => b.Id != "m2-exit"), b => Assert.False(b.PlanBackOffLegs));
    }

    [Fact]
    public void A_planned_back_off_needs_a_leash_margin_below_every_home_monsters_leash()
    {
        var tables = TestCombat.Tables();
        int leash = MonsterDef.Find(tables, "m1.demon")!.LeashRangeCm;
        BotBehaviour Row(bool planned, int marginCm) => new()
        {
            Id = "x", HomeMarkerIds = ["m1.spawn.combat.a"], HealBelowPercent = 50, BackOffBelowPercent = 20,
            PlanBackOffLegs = planned, BackOffLeashMarginCm = marginCm,
        };
        string[] Fields(BotBehaviour row)
        {
            var errors = new RowErrors();
            row.Validate(tables, errors);
            return errors.Items.Select(e => e.Field ?? "").ToArray();
        }

        Assert.Empty(Fields(Row(true, leash - 1)));
        Assert.Equal(["BackOffLeashMarginCm"], Fields(Row(true, leash)));
        Assert.Empty(Fields(Row(false, leash))); // not planned: the margin is unused
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

        var fine = new BotBehaviour
        {
            Id = "y", HomeMarkerIds = ["m1.spawn.combat.a"], LoopSteps = ["fight", "sell"], HealBelowPercent = 50, BackOffBelowPercent = 20,
        };
        errors = new RowErrors();
        fine.Validate(errors);
        Assert.Empty(errors.Items);
    }

    [Fact]
    public void The_re_engage_line_lies_between_the_back_off_and_heal_lines_and_the_recovery_time_covers_the_back_off()
    {
        // The QA kill step's recovery rule (QaFightPlan.Recovered): back off below 20 %, fight again at 40 %, heal below 50 %.
        BotBehaviour Row(int reengage, int recoverMs) => new()
        {
            Id = "x", HomeMarkerIds = ["m1.spawn.combat.a"], HealBelowPercent = 50, BackOffBelowPercent = 20, BackOffMs = 3_000,
            ReengageAbovePercent = reengage, BackOffRecoverMs = recoverMs,
        };
        string[] Fields(BotBehaviour row)
        {
            var errors = new RowErrors();
            row.Validate(errors);
            return errors.Items.Select(e => e.Field ?? "").ToArray();
        }

        Assert.Empty(Fields(Row(40, 30_000)));
        Assert.Empty(Fields(Row(20, 3_000)));
        Assert.Empty(Fields(Row(50, 3_000)));
        Assert.Equal(["ReengageAbovePercent"], Fields(Row(19, 30_000)));
        Assert.Equal(["ReengageAbovePercent"], Fields(Row(51, 30_000)));
        Assert.Equal(["BackOffRecoverMs"], Fields(Row(40, 2_999)));
    }
}
