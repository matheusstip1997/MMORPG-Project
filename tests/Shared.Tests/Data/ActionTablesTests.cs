using Shared.Data;
using Shared.Data.Tables;
using static Shared.Tests.Simulation.ActionTestKit;

namespace Shared.Tests.Data;

/// <summary>Validator rules of ActionDef, ActionEffectDef and ActionBindings (backlog D2, D4), including the cross-table hook.</summary>
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
        set.Add(new ConfigTable(typeof(ActionEffectDef), 2, effects ?? Effects()));
        set.Add(new ConfigTable(typeof(ActionBindings), 1, [bindings ?? Bindings()]));
        set.Add(new ConfigTable(typeof(StartingValuesM1), 1, [new StartingValuesM1
        {
            DodgeDurationMs = 500, ParryWindowMs = 200, DodgeIFramesMs = 250, DodgeStaminaCost = 10,
            NormalAttackStaminaCost = 10, SkillStaminaCostMin = 10, SkillStaminaCostMax = 30,
        }]));
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
    public void An_attack_with_a_strike_or_projectile_may_have_a_parry_color()
    {
        // R-3.3-04, R-3.3-06: colors live on attacks; PvE and PvP may differ.
        var actions = Actions();
        actions[0].ParryClassPve = ParryColor.Green;
        actions[0].ParryClassPvp = ParryColor.Red;
        Assert.Empty(Validate(actions));
        Assert.Equal(ParryColor.Green, actions[0].ParryColorFor(attackerIsMonster: true));
        Assert.Equal(ParryColor.Red, actions[0].ParryColorFor(attackerIsMonster: false));
    }

    [Theory]
    [InlineData(1)] // dodge
    [InlineData(2)] // parry
    public void Only_attacks_and_skills_have_parry_colors(int index)
    {
        var actions = Actions();
        actions[index].ParryClassPvp = ParryColor.Blue;
        SingleError(Validate(actions), "ActionDef", nameof(ActionDef.ParryClassPvp));
    }

    [Fact]
    public void A_colored_action_must_hit_something()
    {
        // The skill only dashes: a color would never be parried.
        var actions = Actions();
        actions[3].ParryClassPve = ParryColor.Yellow;
        SingleError(Validate(actions), "ActionDef", nameof(ActionDef.ParryClassPve));
    }

    [Fact]
    public void An_unknown_parry_color_is_refused()
    {
        var actions = Actions();
        actions[0].ParryClassPve = (ParryColor)9;
        SingleError(Validate(actions), "ActionDef", nameof(ActionDef.ParryClassPve));
    }

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
            Id = "skill.area", ActionId = "skill", Kind = EffectKind.GroundArea, Phase = ActionPhase.Active, Targets = [EffectTarget.Enemies],
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
        var heal = new ActionEffectDef { Id = "heal", ActionId = "skill", Kind = EffectKind.Heal, Phase = ActionPhase.Active,
            Targets = [EffectTarget.Self], HealPercent = 100, DurationMs = 100 };
        SingleError(Validate(effects: [.. Effects(), heal]), "ActionEffectDef", nameof(ActionEffectDef.DurationMs));
        var status = new ActionEffectDef { Id = "haste", ActionId = "skill", Kind = EffectKind.ApplyStatus, Phase = ActionPhase.Active,
            Targets = [EffectTarget.Self] };
        SingleError(Validate(effects: [.. Effects(), status]), "ActionEffectDef", nameof(ActionEffectDef.StatusId));
    }

    [Fact]
    public void Strikes_and_projectiles_need_a_damage_multiplier_and_kind_and_nothing_else_has_them()
    {
        // R-3.4-04 step 1: Atk or Matk times the skill multiplier.
        var effects = Effects();
        effects[0].DamagePercent = 0;
        SingleError(Validate(effects: effects), "ActionEffectDef", nameof(ActionEffectDef.DamagePercent));

        effects = Effects();
        effects[0].DamageKind = EffectDamageKind.None;
        SingleError(Validate(effects: effects), "ActionEffectDef", nameof(ActionEffectDef.DamageKind));

        effects = Effects();
        effects[0].DamagePercent = DamageCalcMax + 1;
        SingleError(Validate(effects: effects), "ActionEffectDef", nameof(ActionEffectDef.DamagePercent));

        // The projectile's own fields (projectileId, poise, flinch) are checked in ProjectileDefTests; this set has no
        // ProjectileDef table, so its projectileId stays empty and only that error is left once the damage is set.
        var projectile = new ActionEffectDef
        {
            Id = "shot", ActionId = "skill", Kind = EffectKind.Projectile, Phase = ActionPhase.Active,
            PoiseDamage = 10, FlinchPower = 10, FlinchMs = 300,
        };
        Assert.Equal(2, Validate(effects: [.. Effects(), projectile])
            .Count(e => e.Table == "ActionEffectDef" && e.Field != nameof(ActionEffectDef.ProjectileId)));
        projectile.DamagePercent = 100;
        projectile.DamageKind = EffectDamageKind.Magic;
        SingleError(Validate(effects: [.. Effects(), projectile]), "ActionEffectDef", nameof(ActionEffectDef.ProjectileId));

        var heal = new ActionEffectDef
        {
            Id = "heal", ActionId = "skill", Kind = EffectKind.Heal, Phase = ActionPhase.Active, DamagePercent = 100, Targets = [EffectTarget.Self], HealPercent = 100,
        };
        SingleError(Validate(effects: [.. Effects(), heal]), "ActionEffectDef", nameof(ActionEffectDef.DamagePercent));
        heal.DamagePercent = 0;
        heal.DamageKind = EffectDamageKind.Physical;
        SingleError(Validate(effects: [.. Effects(), heal]), "ActionEffectDef", nameof(ActionEffectDef.DamageKind));
    }

    private const int DamageCalcMax = Shared.Simulation.Stats.DamageCalc.MaxSkillMultiplierPercent;

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

    [Fact]
    public void Attacks_skills_and_dodges_cost_stamina_and_parries_may_not()
    {
        // R-3.1-04 (no heavy attacks: attacks and skills cost stamina), R-3.3-02 (a dodge costs stamina).
        foreach (int row in new[] { 0, 1, 3 })
        {
            var actions = Actions();
            actions[row].StaminaCost = 0;
            Assert.Contains(Validate(actions), e => (e.Table, e.Field) == ("ActionDef", nameof(ActionDef.StaminaCost)));
        }
        Assert.Equal(0, Actions()[2].StaminaCost); // the sample parry is valid without a cost
    }

    [Fact]
    public void Stamina_costs_follow_the_starting_values()
    {
        // Spec 12.7: normal attack 10, skills 15-30 (here 10-30), dodge 20 (here 10).
        var actions = Actions();
        actions[0].StaminaCost = 12;
        Assert.Contains("NormalAttackStaminaCost", SingleError(Validate(actions), "ActionBindings", nameof(ActionBindings.NormalAttack)).Message);
        actions = Actions();
        actions[1].StaminaCost = 20;
        Assert.Contains("DodgeStaminaCost", SingleError(Validate(actions), "ActionBindings", nameof(ActionBindings.Dodge)).Message);
        actions = Actions();
        actions[3].StaminaCost = 31;
        Assert.Contains("'skill'", SingleError(Validate(actions), "ActionBindings", nameof(ActionBindings.SkillSlots)).Message);
        actions[3].StaminaCost = 30;
        Assert.Empty(Validate(actions));
    }

    [Fact]
    public void Dodge_and_parry_spend_from_their_own_pools_and_nothing_else_does()
    {
        // R-3.3-02, R-3.3-03.
        var cases = new (int Row, ChargePool Pool)[]
        {
            (0, ChargePool.Dodge), (1, ChargePool.None), (1, ChargePool.Parry), (2, ChargePool.Dodge), (3, ChargePool.Parry),
            (3, (ChargePool)9),
        };
        foreach (var (row, pool) in cases)
        {
            var actions = Actions();
            actions[row].ChargePool = pool;
            SingleError(Validate(actions), "ActionDef", nameof(ActionDef.ChargePool));
        }
    }

    [Fact]
    public void Only_a_dodge_has_i_frames_inside_the_action_as_the_starting_values_say()
    {
        // R-3.3-02. The sample dodge: 50/250/200 ms with i-frames from 50 ms for 250 ms (spec 12.7).
        var actions = Actions();
        actions[0].IFramesMs = 100;
        SingleError(Validate(actions), "ActionDef", nameof(ActionDef.IFramesMs));
        actions = Actions();
        actions[2].IFramesStartMs = 50;
        SingleError(Validate(actions), "ActionDef", nameof(ActionDef.IFramesMs));

        actions = Actions();
        actions[1].IFramesStartMs = 251; // ends at 501 ms, the dodge lasts 500
        SingleError(Validate(actions), "ActionDef", nameof(ActionDef.IFramesMs));
        actions[1].IFramesStartMs = 250; // ends exactly at the end
        Assert.Empty(Validate(actions));

        actions = Actions();
        actions[1].IFramesMs = 0;
        var errors = Validate(actions);
        Assert.Contains(errors, e => (e.Table, e.Field) == ("ActionDef", nameof(ActionDef.IFramesMs)));
        Assert.Contains(errors, e => (e.Table, e.Field) == ("ActionBindings", nameof(ActionBindings.Dodge)));

        actions = Actions();
        actions[1].IFramesMs = 200; // spec 12.7: 250 ms
        Assert.Contains("DodgeIFramesMs", SingleError(Validate(actions), "ActionBindings", nameof(ActionBindings.Dodge)).Message);
    }
}
