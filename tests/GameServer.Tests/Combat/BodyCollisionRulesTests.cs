using GameServer.Combat;
using GameServer.Tests.Support;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Simulation;

namespace GameServer.Tests.Combat;

/// <summary>R-2.2-07: the body collision settings in the combat rules and their cross-table rules (CombatRules.TryBuild).</summary>
public class BodyCollisionRulesTests
{
    private static List<ValidationError> Errors(Action<ConfigSet> tune)
    {
        var tables = TestCombat.Tables();
        tune(tables);
        var errors = new List<ValidationError>();
        CombatRules.TryBuild(tables, errors);
        return errors;
    }

    private static T Row<T>(ConfigSet tables, Func<T, bool> pick)
    {
        Assert.True(tables.TryGet(typeof(T), out var table));
        return table.Rows.Cast<T>().Single(pick);
    }

    [Fact]
    public void The_committed_settings_reach_the_rules()
    {
        var rules = TestCombat.Rules();
        Assert.True(rules.Bodies.Enabled);
        Assert.Equal(BodyResponse.Slide, rules.Bodies.WalkResponse);
        Assert.Equal(BodyResponse.Stop, rules.Bodies.DashResponse);
        Assert.Equal(FixedPoint.FromCm(rules.MaxBodyRadiusCm), rules.MaxBodyRadiusRaw);
    }

    [Fact]
    public void Without_the_table_bodies_block_nothing()
    {
        var tables = TestCombat.Tables();
        tables.Add(new ConfigTable(typeof(BodyCollisionSettings), 1, Array.Empty<BodyCollisionSettings>()));
        var errors = new List<ValidationError>();
        var rules = CombatRules.TryBuild(tables, errors);
        Assert.Empty(errors);
        Assert.False(rules!.Bodies.Enabled);
    }

    [Fact]
    public void The_tolerance_must_be_below_the_smallest_body()
    {
        var errors = Errors(t => Row<BodyCollisionSettings>(t, _ => true).OverlapToleranceCm = 40);
        Assert.Contains(errors, e => e.Field == nameof(BodyCollisionSettings.OverlapToleranceCm));
    }

    [Fact]
    public void A_monster_whose_shortest_attack_cannot_reach_past_its_body_is_refused()
    {
        // D3: the demon's claw reaches about 2 m; a 4 m wide demon would stand at contact and never attack.
        var errors = Errors(t => Row<MonsterDef>(t, m => m.Id == TestCombat.MonsterId).BodyRadiusCm = 400);
        Assert.Contains(errors, e => e.Table == "MonsterDef" && e.Message.Contains(TestCombat.MonsterId));
    }

    [Fact]
    public void Two_of_the_largest_bodies_must_fit_the_sweep()
    {
        var errors = Errors(t => Row<MonsterDef>(t, m => m.Id == TestCombat.BossId).BodyRadiusCm = 2_100);
        Assert.Contains(errors, e => e.Table == "BodyCollisionSettings" && e.Message.Contains("sweep"));
    }
}
