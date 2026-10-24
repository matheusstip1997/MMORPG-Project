using Shared.Data;
using Shared.Data.Tables;
using static Shared.Tests.Simulation.ActionTestKit;

namespace Shared.Tests.Data;

/// <summary>
/// Validator rules of backlog D6: <c>StatusEffectDef</c>, <c>StatusModifierDef</c>, <c>BuffIconRegistry</c> (spec 3.7, 9.7),
/// the version 2 fields of <c>ActionEffectDef</c> (element R-3.4-07, target filter R-3.1-07, heal amount, cleanse kind) with
/// their hit shapes, the element list of <c>ElementCaps</c>, and optional fields left out of a row.
/// </summary>
public class StatusTablesTests
{
    private static ActionDef[] Actions() =>
    [
        Action("skill", 101, ActionKind.Skill, 200, 100, 300),
    ];

    private static StatusEffectDef Buff(string id = "buff", int netId = 1) => new()
    {
        Id = id, NetId = netId, Kind = StatusEffectKind.Buff, DurationMs = 10_000, MaxStacks = 1, Dispellable = true, Tags = ["a"],
    };

    private static StatusEffectDef Burn() => new()
    {
        Id = "burn", NetId = 2, Kind = StatusEffectKind.Debuff, DurationMs = 4_000, MaxStacks = 1, Dispellable = true,
        TickIntervalMs = 500, TickDamagePercent = 20, TickDamageKind = EffectDamageKind.Magic, TickElement = Element.Fire,
    };

    private static StatusModifierDef Modifier(string id = "buff.def", string status = "buff", string stat = "def") => new()
    {
        Id = id, StatusId = status, Stat = stat, Percent = 10,
    };

    private static BuffIconRegistry Icon(string id) => new() { Id = id, IconAddress = "", SortOrder = 1 };

    private static ActionEffectDef Heal(params EffectTarget[] targets) => new()
    {
        Id = "skill.heal", ActionId = "skill", Kind = EffectKind.Heal, Phase = ActionPhase.Active, Targets = targets, HealPercent = 150,
    };

    private static ActionEffectDef ApplyBuff(params EffectTarget[] targets) => new()
    {
        Id = "skill.apply", ActionId = "skill", Kind = EffectKind.ApplyStatus, Phase = ActionPhase.Active, Targets = targets, StatusId = "buff",
    };

    private static HitShapeDef Circle(string effectId) => new()
    {
        Id = effectId + ".circle", EffectId = effectId, Kind = HitShapeKind.Capsule, RadiusCm = 500,
    };

    private static List<ValidationError> Validate(StatusEffectDef[]? statuses = null, StatusModifierDef[]? modifiers = null,
        BuffIconRegistry[]? icons = null, ActionEffectDef[]? effects = null, HitShapeDef[]? shapes = null)
    {
        statuses ??= [Buff(), Burn()];
        var set = new ConfigSet();
        set.Add(new ConfigTable(typeof(ActionDef), 1, Actions()));
        set.Add(new ConfigTable(typeof(ActionEffectDef), 2, effects ?? []));
        set.Add(new ConfigTable(typeof(HitShapeDef), 1, shapes ?? []));
        set.Add(new ConfigTable(typeof(StatDef), 1, new[] { new StatDef { Id = "def", Min = 0, Max = 100 }, new StatDef { Id = "atk", Min = 0, Max = 100 } }
            .Concat(Enumerable.Range(0, 5).Select(i => new StatDef { Id = "s" + i, Min = 0, Max = 100 })).ToArray()));
        set.Add(new ConfigTable(typeof(StatusEffectDef), 1, statuses));
        set.Add(new ConfigTable(typeof(StatusModifierDef), 1, modifiers ?? [Modifier()]));
        set.Add(new ConfigTable(typeof(BuffIconRegistry), 1, icons ?? statuses.Select(s => Icon(s.Id)).ToArray()));
        return TableValidator.Validate(set).ToList();
    }

    private static ValidationError SingleError(List<ValidationError> errors, string table, string field)
    {
        var error = Assert.Single(errors);
        Assert.Equal((table, field), (error.Table, error.Field));
        return error;
    }

    [Fact]
    public void The_sample_statuses_are_valid() => Assert.Empty(Validate());

    [Fact]
    public void A_status_is_a_buff_or_a_debuff()
    {
        var buff = Buff();
        buff.Kind = StatusEffectKind.None;
        SingleError(Validate([buff, Burn()]), "StatusEffectDef", nameof(StatusEffectDef.Kind));
    }

    [Fact]
    public void A_status_lasts_and_its_ticks_fit_in_it()
    {
        var buff = Buff();
        buff.DurationMs = 0;
        SingleError(Validate([buff, Burn()]), "StatusEffectDef", nameof(StatusEffectDef.DurationMs));
        var burn = Burn();
        burn.TickIntervalMs = 5_000;
        SingleError(Validate([Buff(), burn]), "StatusEffectDef", nameof(StatusEffectDef.TickIntervalMs));
        buff = Buff();
        buff.MaxStacks = StatusEffectDef.MaxStackLimit + 1;
        SingleError(Validate([buff, Burn()]), "StatusEffectDef", nameof(StatusEffectDef.MaxStacks));
    }

    [Fact]
    public void A_periodic_status_either_damages_or_heals()
    {
        var burn = Burn();
        burn.TickHealPercent = 10;
        SingleError(Validate([Buff(), burn]), "StatusEffectDef", nameof(StatusEffectDef.TickDamagePercent));
        burn = Burn();
        burn.TickDamageKind = EffectDamageKind.None;
        SingleError(Validate([Buff(), burn]), "StatusEffectDef", nameof(StatusEffectDef.TickDamageKind));
        burn = Burn();
        burn.TickIntervalMs = 0;
        Assert.Contains(Validate([Buff(), burn]), e => e.Field == nameof(StatusEffectDef.TickIntervalMs));
        var buff = Buff();
        buff.TickElement = Element.Ice; // no tick damage
        SingleError(Validate([buff, Burn()]), "StatusEffectDef", nameof(StatusEffectDef.TickElement));
    }

    [Fact]
    public void A_status_must_do_something_and_have_a_buff_row_icon()
    {
        SingleError(Validate(modifiers: []), "StatusEffectDef", nameof(StatusEffectDef.Id)); // the buff has no modifier
        var error = SingleError(Validate(icons: [Icon("buff")]), "StatusEffectDef", nameof(StatusEffectDef.Id));
        Assert.Contains("BuffIconRegistry", error.Message);
    }

    [Fact]
    public void Net_ids_are_unique_and_tags_are_not_repeated()
    {
        var errors = Validate([Buff(), Buff("other", 1), Burn()], modifiers: [Modifier(), Modifier("other.def", "other")]);
        Assert.NotEmpty(errors);
        Assert.All(errors, e => Assert.Equal(("StatusEffectDef", "NetId"), (e.Table, e.Field)));
        var buff = Buff();
        buff.Tags = ["a", "a"];
        SingleError(Validate([buff, Burn()]), "StatusEffectDef", nameof(StatusEffectDef.Tags));
    }

    [Fact]
    public void A_modifier_changes_something_once_per_stat_and_at_most_four_per_status()
    {
        var empty = Modifier();
        empty.Percent = 0;
        SingleError(Validate(modifiers: [empty]), "StatusModifierDef", nameof(StatusModifierDef.Flat));
        Assert.Equal(2, Validate(modifiers: [Modifier(), Modifier("buff.def2")]).Count(e => e.Field == nameof(StatusModifierDef.Stat)));
        var five = Enumerable.Range(0, StatusModifierDef.MaxPerStatus + 1)
            .Select(i => new StatusModifierDef { Id = "m" + i, StatusId = "buff", Stat = "s" + i, Flat = i + 1 }).ToArray();
        Assert.Contains(Validate(modifiers: five), e => e.Field == nameof(StatusModifierDef.StatusId));
        SingleError(Validate(modifiers: [Modifier(stat: "nothing")]), "StatusModifierDef", nameof(StatusModifierDef.Stat));
    }

    [Fact]
    public void An_icon_names_a_status_and_a_clean_address()
    {
        var errors = Validate(icons: [Icon("buff"), Icon("burn"), Icon("nothing")]);
        Assert.Equal(ValidationCodes.BrokenRef, SingleError(errors, "BuffIconRegistry", nameof(BuffIconRegistry.Id)).Code);
        var icon = Icon("buff");
        icon.IconAddress = " Icons/Buff";
        SingleError(Validate(icons: [icon, Icon("burn")]), "BuffIconRegistry", nameof(BuffIconRegistry.IconAddress));
    }

    [Fact]
    public void A_heal_names_its_targets_and_its_amount_and_never_targets_enemies()
    {
        Assert.Empty(Validate(effects: [Heal(EffectTarget.Self)]));
        SingleError(Validate(effects: [Heal()]), "ActionEffectDef", nameof(ActionEffectDef.Targets));
        var heal = Heal(EffectTarget.Self);
        heal.HealPercent = 0;
        SingleError(Validate(effects: [heal]), "ActionEffectDef", nameof(ActionEffectDef.HealPercent));
        SingleError(Validate(effects: [Heal(EffectTarget.Self, EffectTarget.Enemies)], shapes: [Circle("skill.heal")]),
            "ActionEffectDef", nameof(ActionEffectDef.Targets));
        SingleError(Validate(effects: [Heal(EffectTarget.Self, EffectTarget.Self)]), "ActionEffectDef", nameof(ActionEffectDef.Targets));
    }

    [Fact]
    public void Effects_that_reach_others_need_shapes_and_self_only_ones_have_none()
    {
        // R-3.1-07: hit shapes and a target filter; "a self effect needs no shape" (skill design, backlog E).
        Assert.Empty(Validate(effects: [Heal(EffectTarget.Self, EffectTarget.Allies)], shapes: [Circle("skill.heal")]));
        SingleError(Validate(effects: [Heal(EffectTarget.Allies)]), "ActionEffectDef", nameof(ActionEffectDef.Id));
        SingleError(Validate(effects: [ApplyBuff(EffectTarget.Self)], shapes: [Circle("skill.apply")]), "HitShapeDef",
            nameof(HitShapeDef.EffectId));
        Assert.Empty(Validate(effects: [ApplyBuff(EffectTarget.Enemies)], shapes: [Circle("skill.apply")]));
    }

    [Fact]
    public void Only_filtered_kinds_have_targets_and_only_heals_a_heal_amount()
    {
        var strike = Effect("skill.strike", "skill", EffectKind.Strike, ActionPhase.Active, 0);
        strike.Targets = [EffectTarget.Enemies];
        SingleError(Validate(effects: [strike], shapes: [Circle("skill.strike")]), "ActionEffectDef", nameof(ActionEffectDef.Targets));
        strike = Effect("skill.strike", "skill", EffectKind.Strike, ActionPhase.Active, 0);
        strike.HealPercent = 10;
        SingleError(Validate(effects: [strike], shapes: [Circle("skill.strike")]), "ActionEffectDef", nameof(ActionEffectDef.HealPercent));
    }

    [Fact]
    public void Only_damaging_effects_have_an_element()
    {
        // R-3.4-07: strikes, projectiles and ground areas.
        var strike = Effect("skill.strike", "skill", EffectKind.Strike, ActionPhase.Active, 0);
        strike.Element = Element.Holy;
        Assert.Empty(Validate(effects: [strike], shapes: [Circle("skill.strike")]));
        var heal = Heal(EffectTarget.Self);
        heal.Element = Element.Holy;
        SingleError(Validate(effects: [heal]), "ActionEffectDef", nameof(ActionEffectDef.Element));
        strike.Element = (Element)11;
        SingleError(Validate(effects: [strike], shapes: [Circle("skill.strike")]), "ActionEffectDef", nameof(ActionEffectDef.Element));
    }

    [Fact]
    public void An_apply_names_an_existing_status_and_a_remove_names_a_status_or_a_kind()
    {
        var apply = ApplyBuff(EffectTarget.Self);
        apply.StatusId = "nothing";
        Assert.Equal(ValidationCodes.BrokenRef, SingleError(Validate(effects: [apply]), "ActionEffectDef", nameof(ActionEffectDef.StatusId)).Code);
        apply = ApplyBuff(EffectTarget.Self);
        apply.StatusKind = StatusEffectKind.Buff;
        SingleError(Validate(effects: [apply]), "ActionEffectDef", nameof(ActionEffectDef.StatusKind));

        var cleanse = new ActionEffectDef
        {
            Id = "skill.cleanse", ActionId = "skill", Kind = EffectKind.RemoveStatus, Phase = ActionPhase.Active,
            Targets = [EffectTarget.Self], StatusKind = StatusEffectKind.Debuff,
        };
        Assert.Empty(Validate(effects: [cleanse]));
        cleanse.StatusId = "burn";
        SingleError(Validate(effects: [cleanse]), "ActionEffectDef", nameof(ActionEffectDef.StatusId)); // both
        cleanse.StatusKind = StatusEffectKind.None;
        Assert.Empty(Validate(effects: [cleanse])); // a named status
        cleanse.StatusId = "";
        SingleError(Validate(effects: [cleanse]), "ActionEffectDef", nameof(ActionEffectDef.StatusId)); // neither
    }

    [Fact]
    public void Element_caps_rows_are_named_after_the_elements()
    {
        var errors = new RowErrors();
        new ElementCaps { Id = "physical", ResistStat = "x", DamageResistMinPercent = 0, DamageResistMaxPercent = 0 }.Validate(errors);
        Assert.Contains(errors.Items, e => e.Field == nameof(ElementCaps.Id));
        errors = new RowErrors();
        new ElementCaps { Id = "blood", ResistStat = "x", DamageResistMinPercent = 0, DamageResistMaxPercent = 0 }.Validate(errors);
        Assert.Empty(errors.Items);
        Assert.Equal(10, Elements.Count);
        Assert.True(Elements.TryParse("lightning", out var lightning));
        Assert.Equal(Element.Lightning, lightning);
        Assert.False(Elements.TryParse("none", out _));
    }

    [Fact]
    public void Optional_fields_may_be_left_out_and_keep_their_defaults()
    {
        // A row written before version 2 (or by another branch) without element, targets, healPercent, statusKind.
        const string json = """
            { "table": "ActionEffectDef", "version": 2, "rows": [
              { "id": "a.strike", "actionId": "a", "kind": "strike", "phase": "active", "offsetMs": 0, "distanceCm": 0, "durationMs": 0,
                "tickIntervalMs": 0, "poiseDamage": 1, "flinchPower": 1, "flinchMs": 1, "statusId": "", "damagePercent": 100,
                "damageKind": "physical", "projectileId": "" } ] }
            """;
        var errors = new List<ValidationError>();
        var table = ConfigTableReader.ReadTable(json, typeof(ActionEffectDef), errors);
        Assert.Empty(errors);
        var row = (ActionEffectDef)Assert.Single(table!.Rows);
        Assert.Equal((Element.None, 0, 0, StatusEffectKind.None), (row.Element, row.Targets.Length, row.HealPercent, row.StatusKind));

        // A required field is still required.
        errors.Clear();
        ConfigTableReader.ReadTable(json.Replace("\"poiseDamage\": 1, ", ""), typeof(ActionEffectDef), errors);
        Assert.Equal(ValidationCodes.MissingField, Assert.Single(errors).Code);
    }
}
