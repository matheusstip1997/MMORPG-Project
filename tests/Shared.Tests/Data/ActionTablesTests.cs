using Shared.Data;
using Shared.Data.Tables;
using static Shared.Tests.Simulation.ActionTestKit;

namespace Shared.Tests.Data;

/// <summary>Validator rules of ActionDef, ActionEffectDef and ActionBindings (backlog D2), including the cross-table hook.</summary>
public class ActionTablesTests
{
    private static ActionDef[] Actions() =>
    [
        Action("attack", 1, ActionKind.Attack, 300, 100, 300, cancelableBy: ["dodge"], cancelFrom: ActionPhase.Recovery),
        Action("dodge", 2, ActionKind.Dodge, 50, 250, 200),
        Action("parry", 3, ActionKind.Parry, 50, 200, 250),
        Action("skill", 101, ActionKind.Skill, 200, 100, 300, channel: 1000),
    ];

    private static ActionEffectDef[] Effects() =>
    [
        Effect("attack.strike", "attack", EffectKind.Strike, ActionPhase.Active, 0),
        Effect("skill.dash", "skill", EffectKind.Dash, ActionPhase.Startup, 0, distanceCm: 300, durationMs: 200),
    ];

    private static ActionBindings Bindings() => new()
    {
        NormalAttack = "attack", Dodge = "dodge", Parry = "parry", SkillSlots = ["skill", ""],
    };

    private static List<ValidationError> Validate(ActionDef[]? actions = null, ActionEffectDef[]? effects = null,
        ActionBindings? bindings = null)
    {
        var set = new ConfigSet();
        set.Add(new ConfigTable(typeof(ActionDef), 1, actions ?? Actions()));
        set.Add(new ConfigTable(typeof(ActionEffectDef), 1, effects ?? Effects()));
        set.Add(new ConfigTable(typeof(ActionBindings), 1, [bindings ?? Bindings()]));
        set.Add(new ConfigTable(typeof(StartingValuesM1), 1, [new StartingValuesM1 { DodgeDurationMs = 500, ParryWindowMs = 200 }]));
        // StartingValuesM1 is mostly zeros here: keep only the errors of the action tables.
        return TableValidator.Validate(set).Where(e => e.Table != "StartingValuesM1").ToList();
    }

    private static ValidationError SingleError(List<ValidationError> errors, string table, string field)
    {
        var error = Assert.Single(errors);
        Assert.Equal((table, field), (error.Table, error.Field));
        return error;
    }

    [Fact]
    public void The_sample_tables_are_valid() => Assert.Empty(Validate());

    [Fact]
    public void Net_ids_are_unique()
    {
        var actions = Actions();
        actions[3].NetId = 1;
        var errors = Validate(actions);
        Assert.Equal(2, errors.Count); // both rows report it
        Assert.All(errors, e => Assert.Equal(nameof(ActionDef.NetId), e.Field));
    }

    [Fact]
    public void An_action_must_last_and_its_buffer_window_fit_in_it()
    {
        var actions = Actions();
        actions[3] = Action("skill", 101, ActionKind.Skill, 0, 0, 0);
        Assert.Contains(Validate(actions, effects: [Effects()[0]]), e => e.Field == nameof(ActionDef.StartupMs));

        actions = Actions();
        actions[0].BufferWindowMs = 701;
        SingleError(Validate(actions), "ActionDef", nameof(ActionDef.BufferWindowMs));
    }

    [Fact]
    public void Cancel_lists_point_to_existing_actions_once()
    {
        var actions = Actions();
        actions[0].CancelableBy = ["nothing"];
        Assert.Equal(ValidationCodes.BrokenRef, SingleError(Validate(actions), "ActionDef", nameof(ActionDef.CancelableBy)).Code);
        actions[0].CancelableBy = ["dodge", "dodge"];
        SingleError(Validate(actions), "ActionDef", nameof(ActionDef.CancelableBy));
    }

    [Fact]
    public void Super_armor_needs_an_existing_phase_and_never_on_a_dodge()
    {
        var actions = Actions();
        actions[0].SuperArmorPhases = [ActionPhase.Channel];
        SingleError(Validate(actions), "ActionDef", nameof(ActionDef.SuperArmorPhases));
        actions = Actions();
        actions[1].SuperArmorPhases = [ActionPhase.Startup];
        SingleError(Validate(actions), "ActionDef", nameof(ActionDef.SuperArmorPhases));
    }

    [Fact]
    public void An_effect_sits_inside_an_existing_phase()
    {
        var effects = Effects();
        effects[0].Phase = ActionPhase.Channel; // the attack has no channel
        SingleError(Validate(effects: effects), "ActionEffectDef", nameof(ActionEffectDef.Phase));
        effects = Effects();
        effects[0].OffsetMs = 100; // Active is 100 ms
        SingleError(Validate(effects: effects), "ActionEffectDef", nameof(ActionEffectDef.OffsetMs));
        effects[0].OffsetMs = 99;
        Assert.Empty(Validate(effects: effects));
    }

    [Fact]
    public void A_dash_must_move_take_time_and_end_inside_the_action()
    {
        var effects = Effects();
        effects[1].DistanceCm = 0;
        SingleError(Validate(effects: effects), "ActionEffectDef", nameof(ActionEffectDef.DistanceCm));
        effects = Effects();
        effects[1].DurationMs = 1601; // the skill lasts 1600 ms
        SingleError(Validate(effects: effects), "ActionEffectDef", nameof(ActionEffectDef.DurationMs));
        effects[1].DurationMs = 1600;
        Assert.Empty(Validate(effects: effects));
    }

    [Fact]
    public void Every_strike_carries_poise_damage_and_flinch()
    {
        foreach (var field in new[] { nameof(ActionEffectDef.PoiseDamage), nameof(ActionEffectDef.FlinchPower), nameof(ActionEffectDef.FlinchMs) })
        {
            var effects = Effects();
            typeof(ActionEffectDef).GetProperty(field)!.SetValue(effects[0], 0);
            SingleError(Validate(effects: effects), "ActionEffectDef", field); // R-3.1-08, R-3.3-07
        }
    }

    [Fact]
    public void Ground_effects_never_flinch()
    {
        var area = new ActionEffectDef
        {
            Id = "skill.area", ActionId = "skill", Kind = EffectKind.GroundArea, Phase = ActionPhase.Active,
            DurationMs = 3000, TickIntervalMs = 500, PoiseDamage = 5, FlinchPower = 1,
        };
        SingleError(Validate(effects: [.. Effects(), area]), "ActionEffectDef", nameof(ActionEffectDef.FlinchPower)); // R-3.3-09
        area.FlinchPower = 0;
        area.TickIntervalMs = 0;
        SingleError(Validate(effects: [.. Effects(), area]), "ActionEffectDef", nameof(ActionEffectDef.TickIntervalMs));
    }

    [Fact]
    public void Fields_a_kind_does_not_use_must_be_empty()
    {
        var effects = Effects();
        effects[0].DistanceCm = 10;
        SingleError(Validate(effects: effects), "ActionEffectDef", nameof(ActionEffectDef.DistanceCm));
        var heal = new ActionEffectDef { Id = "heal", ActionId = "skill", Kind = EffectKind.Heal, Phase = ActionPhase.Active, StatusId = "x" };
        SingleError(Validate(effects: [.. Effects(), heal]), "ActionEffectDef", nameof(ActionEffectDef.StatusId));
        var status = new ActionEffectDef { Id = "haste", ActionId = "skill", Kind = EffectKind.ApplyStatus, Phase = ActionPhase.Active };
        SingleError(Validate(effects: [.. Effects(), status]), "ActionEffectDef", nameof(ActionEffectDef.StatusId));
    }

    [Fact]
    public void An_action_has_at_most_eight_effects()
    {
        var many = Enumerable.Range(0, ActionEffectDef.MaxEffectsPerAction + 1)
            .Select(i => Effect("attack.strike" + i, "attack", EffectKind.Strike, ActionPhase.Active, 0)).ToArray();
        var errors = Validate(effects: many);
        Assert.Equal(many.Length, errors.Count);
        Assert.All(errors, e => Assert.Equal(nameof(ActionEffectDef.ActionId), e.Field));
    }

    [Fact]
    public void Bindings_point_to_actions_of_the_right_kind()
    {
        var bindings = Bindings();
        bindings.Dodge = "parry";
        var errors = Validate(bindings: bindings);
        Assert.Contains(errors, e => e.Table == "ActionBindings" && e.Field == nameof(ActionBindings.Dodge) && e.Message.Contains("Parry"));

        bindings = Bindings();
        bindings.SkillSlots = ["attack"];
        SingleError(Validate(bindings: bindings), "ActionBindings", nameof(ActionBindings.SkillSlots));

        bindings.SkillSlots = ["missing"];
        Assert.Equal(ValidationCodes.BrokenRef, SingleError(Validate(bindings: bindings), "ActionBindings", nameof(ActionBindings.SkillSlots)).Code);

        bindings.SkillSlots = Enumerable.Repeat("", ActionBindings.MaxSkillSlots + 1).ToArray();
        SingleError(Validate(bindings: bindings), "ActionBindings", nameof(ActionBindings.SkillSlots));
    }

    [Fact]
    public void Dodge_and_parry_follow_the_starting_values()
    {
        var actions = Actions();
        actions[1].RecoveryMs = 250; // 550 ms, spec 12.7 says 500
        SingleError(Validate(actions), "ActionBindings", nameof(ActionBindings.Dodge));
        actions = Actions();
        actions[2].ActiveMs = 150; // spec 12.7: a 200 ms window
        SingleError(Validate(actions), "ActionBindings", nameof(ActionBindings.Parry));
    }

    [Fact]
    public void Lists_of_references_are_checked_item_by_item()
    {
        var bindings = Bindings();
        bindings.SkillSlots = ["skill", "", "nope", "skill"];
        var error = SingleError(Validate(bindings: bindings), "ActionBindings", nameof(ActionBindings.SkillSlots));
        Assert.Contains("'nope'", error.Message);
    }
}
