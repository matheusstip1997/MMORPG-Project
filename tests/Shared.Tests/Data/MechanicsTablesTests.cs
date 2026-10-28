using Shared.Data;
using Shared.Data.Tables;

namespace Shared.Tests.Data;

/// <summary>
/// WP-M2-9 validator rules on the committed tables (spec 3.7 "M2 status list", 3.8 "M2 numbers"): status controls and
/// bursts, the stack caps of <c>StatusSettings</c> (user answer 2026-10-28: maxStacks limits), on-hit statuses, Impact
/// effects and their projectile, cleanse counts, statuses that end with their phase, and the boss cap of R-3.8-08.
/// Each test changes one committed row and expects exactly the error it breaks.
/// </summary>
public class MechanicsTablesTests
{
    private static ConfigSet LoadRepository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Mmorpg.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var tables = Path.Combine(directory!.FullName, "data", "tables");
        var errors = new List<ValidationError>();
        var set = ConfigTableReader.LoadAndValidate(ConfigTables.All, name =>
        {
            var path = Path.Combine(tables, name + ".json");
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }, errors);
        Assert.Empty(errors);
        return set;
    }

    private static T Row<T>(ConfigSet set, Func<T, bool> match)
    {
        Assert.True(set.TryGet(typeof(T), out var table));
        return table.Rows.Cast<T>().Single(match);
    }

    private static StatusEffectDef Status(ConfigSet set, string id) => Row<StatusEffectDef>(set, s => s.Id == id);

    private static ActionEffectDef Effect(ConfigSet set, string id) => Row<ActionEffectDef>(set, e => e.Id == id);

    private static ValidationError OneError(ConfigSet set, string table, string field)
    {
        var errors = TableValidator.Validate(set).ToList();
        var error = Assert.Single(errors);
        Assert.Equal((table, field), (error.Table, error.Field));
        return error;
    }

    [Fact]
    public void The_committed_mechanics_rows_are_valid_and_every_wave_1_mechanic_is_wired()
    {
        var set = LoadRepository();
        Assert.Empty(TableValidator.Validate(set));
        Assert.Equal(StatusControl.Stun, Status(set, "stunned").Control);
        Assert.Equal(StatusControl.Blind, Status(set, "blinded").Control);
        Assert.Equal(StatusControl.Provoke, Status(set, "provoked").Control);
        Assert.Equal(500, Status(set, "stunned").DurationMs); // spec 3.5 wave 1 decisions: Heavy Strike's 0.5 s
        Assert.Equal(15, Status(set, "bloodLoss").BurstMaxHpPercent); // spec 3.8 M2 numbers
        Assert.Equal("stunned", Effect(set, "skill.swordman.heavyStrike.strike").StatusId);
        Assert.Equal("blinded", Effect(set, "skill.assist.flash.strike").StatusId);
        Assert.Equal("provoked", Effect(set, "skill.warrior.provoke.apply").StatusId);
        Assert.Equal("focus", Effect(set, "skill.swordman.focus.apply").StatusId);
        Assert.True(Effect(set, "skill.mage.concentration.apply").EndsWithPhase);
        Assert.Equal("skill.archer.bombThrow.explosion", Effect(set, "skill.archer.bombThrow.projectile").ImpactEffectId);
        Assert.Equal("skill.mage.rockThrow.explosion", Effect(set, "skill.mage.rockThrow.projectile").ImpactEffectId);
        Assert.Equal(2, Effect(set, "skill.assist.simpleCleanse.remove").RemoveCount);
        Assert.All(new[] { 1, 2, 3 }, n => Assert.Equal("defenceDown", Effect(set, "skill.knight.piercingSequence.strike" + n).StatusId));
        Assert.Contains(Row<EffectBuildUpDef>(set, r => r.EffectId == "skill.swordman.bloodStrike.strike").BuildUpId, new[] { "bloodLoss" });
        var settings = StatusSettings.Find(set)!;
        Assert.Equal((1, 3, 10), (settings.ControlMaxStacks, settings.StatDebuffMaxStacks, settings.BuffRowMaxIcons));
    }

    [Fact]
    public void A_control_status_stacks_at_most_the_control_cap_and_a_stat_debuff_at_most_the_debuff_cap()
    {
        var set = LoadRepository();
        Status(set, "stunned").MaxStacks = 2;
        OneError(set, "StatusEffectDef", nameof(StatusEffectDef.MaxStacks));

        set = LoadRepository();
        Status(set, "defenceDown").MaxStacks = 4;
        OneError(set, "StatusEffectDef", nameof(StatusEffectDef.MaxStacks));

        set = LoadRepository();
        Status(set, "poison").MaxStacks = 5; // a damage-over-time debuff without modifiers is not a stat debuff
        Assert.Empty(TableValidator.Validate(set));
    }

    [Fact]
    public void A_control_status_is_a_debuff_and_a_status_with_only_a_control_or_a_burst_is_valid()
    {
        var set = LoadRepository();
        Status(set, "provoked").Kind = StatusEffectKind.Buff;
        OneError(set, "StatusEffectDef", nameof(StatusEffectDef.Control));

        set = LoadRepository();
        Status(set, "stunned").Control = StatusControl.None; // no modifier, tick, burst or control left
        OneError(set, "StatusEffectDef", nameof(StatusEffectDef.Id));
    }

    [Fact]
    public void A_burst_of_power_needs_a_damage_kind()
    {
        var set = LoadRepository();
        Status(set, "cold").TickDamageKind = EffectDamageKind.None;
        Status(set, "cold").TickElement = Element.None;
        OneError(set, "StatusEffectDef", nameof(StatusEffectDef.TickDamageKind));
    }

    [Fact]
    public void An_on_hit_status_is_a_debuff()
    {
        var set = LoadRepository();
        Effect(set, "skill.swordman.heavyStrike.strike").StatusId = "focus";
        OneError(set, "ActionEffectDef", nameof(ActionEffectDef.StatusId));
    }

    [Fact]
    public void An_impact_is_named_by_exactly_one_projectile_of_its_action()
    {
        var set = LoadRepository();
        Effect(set, "skill.mage.rockThrow.projectile").ImpactEffectId = "";
        OneError(set, "ActionEffectDef", nameof(ActionEffectDef.Id)); // the explosion is named by none

        set = LoadRepository();
        Effect(set, "skill.mage.rockThrow.projectile").ImpactEffectId = "skill.swordman.heavyStrike.strike";
        var errors = TableValidator.Validate(set).ToList();
        Assert.Contains(errors, e => e.Field == nameof(ActionEffectDef.ImpactEffectId)); // not an Impact of its action
        Assert.Contains(errors, e => e.Field == nameof(ActionEffectDef.Id)); // and the explosion is named by none

        set = LoadRepository();
        Effect(set, "skill.swordman.heavyStrike.strike").ImpactEffectId = "skill.mage.rockThrow.explosion";
        Assert.Contains(TableValidator.Validate(set), e => e.Field == nameof(ActionEffectDef.ImpactEffectId)); // a strike has none
    }

    [Fact]
    public void A_remove_count_needs_a_remove_by_kind_and_ends_with_phase_a_self_only_apply_that_lasts_the_phase()
    {
        var set = LoadRepository();
        Effect(set, "skill.assist.simpleCleanse.remove").StatusKind = StatusEffectKind.None;
        OneError(set, "ActionEffectDef", nameof(ActionEffectDef.RemoveCount));

        set = LoadRepository();
        Effect(set, "skill.assist.lifeRegeneration.apply").EndsWithPhase = true; // reaches allies too
        OneError(set, "ActionEffectDef", nameof(ActionEffectDef.EndsWithPhase));

        set = LoadRepository();
        Status(set, "concentration").DurationMs = 1_000; // shorter than the 3 s channel it is held by
        OneError(set, "ActionEffectDef", nameof(ActionEffectDef.StatusId));
    }

    [Fact]
    public void A_boss_caps_percent_of_max_hp_effects()
    {
        var set = LoadRepository();
        Row<MonsterDef>(set, m => m.Id == "m1.cyclopsBoss").MaxHpEffectCapPercent = 0;
        OneError(set, "MonsterDef", nameof(MonsterDef.MaxHpEffectCapPercent));
    }

    [Fact]
    public void Optional_mechanics_fields_may_be_left_out()
    {
        var errors = new List<ValidationError>();
        var status = ConfigTableReader.ReadTable(
            "{\"table\":\"StatusEffectDef\",\"version\":1,\"rows\":[{\"id\":\"x\",\"netId\":1,\"kind\":\"buff\",\"durationMs\":100," +
            "\"maxStacks\":1,\"dispellable\":true,\"tags\":[],\"tickIntervalMs\":0,\"tickDamagePercent\":0,\"tickDamageKind\":\"none\"," +
            "\"tickElement\":\"none\",\"tickHealPercent\":0}]}", typeof(StatusEffectDef), errors);
        Assert.Empty(errors);
        var row = (StatusEffectDef)Assert.Single(status!.Rows);
        Assert.Equal((StatusControl.None, 0, 0), (row.Control, row.BurstDamagePercent, row.BurstMaxHpPercent));
    }
}
