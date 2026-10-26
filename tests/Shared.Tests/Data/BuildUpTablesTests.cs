using Shared.Data;
using Shared.Data.Tables;
using static Shared.Tests.Simulation.ActionTestKit;

namespace Shared.Tests.Data;

/// <summary>
/// Validator rules of backlog D6 part 2 (WP11): <c>BuildUpDef</c> and <c>EffectBuildUpDef</c> (spec 3.8), the ground area
/// damage and heal fields, <c>buildUpReduction</c> (R-3.8-04) and <c>visualId</c> of <c>ActionEffectDef</c>.
/// </summary>
public class BuildUpTablesTests
{
    private static ActionDef[] Actions() =>
    [
        Action("skill", 101, ActionKind.Skill, 200, 100, 300),
    ];

    private static StatusEffectDef BurnStatus() => new()
    {
        Id = "burn", NetId = 4, Kind = StatusEffectKind.Debuff, DurationMs = 4_000, MaxStacks = 1, Dispellable = true,
        TickIntervalMs = 500, TickDamagePercent = 20, TickDamageKind = EffectDamageKind.Magic, TickElement = Element.Fire,
    };

    private static BuildUpDef Burn(string id = "burn", int netId = 1, Element element = Element.Fire) => new()
    {
        Id = id, NetId = netId, Element = element, StatusId = "burn", DecayDelayMs = 3000, DecayPerSec = 10,
        ResistScalePercent = 100, ResistMinPercent = -50, ResistMaxPercent = 75,
        RepeatPenaltyWindowMs = 10_000, RepeatPenaltyGainPercents = [50, 25],
    };

    private static ActionEffectDef Strike(string id = "skill.strike") => Effect(id, "skill", EffectKind.Strike, ActionPhase.Active, 0);

    private static ActionEffectDef Area(params EffectTarget[] targets) => new()
    {
        Id = "skill.area", ActionId = "skill", Kind = EffectKind.GroundArea, Phase = ActionPhase.Active, Targets = targets,
        DurationMs = 2000, TickIntervalMs = 250, PoiseDamage = 2,
        DamagePercent = targets.Contains(EffectTarget.Enemies) ? 25 : 0,
        DamageKind = targets.Contains(EffectTarget.Enemies) ? EffectDamageKind.Magic : EffectDamageKind.None,
        HealPercent = targets.Contains(EffectTarget.Allies) || targets.Contains(EffectTarget.Self) ? 30 : 0,
    };

    private static ActionEffectDef Cleanse(int reduction, StatusEffectKind kind = StatusEffectKind.None) => new()
    {
        Id = "skill.cleanse", ActionId = "skill", Kind = EffectKind.RemoveStatus, Phase = ActionPhase.Active,
        Targets = [EffectTarget.Self], StatusKind = kind, BuildUpReduction = reduction,
    };

    private static EffectBuildUpDef Change(string effectId, int amount = 25, string id = "", string buildUp = "burn") => new()
    {
        Id = id.Length > 0 ? id : effectId + "." + buildUp, EffectId = effectId, BuildUpId = buildUp, Amount = amount,
    };

    private static HitShapeDef Circle(string effectId, string suffix = "circle") => new()
    {
        Id = effectId + "." + suffix, EffectId = effectId, Kind = HitShapeKind.Capsule, RadiusCm = 300,
    };

    private static List<ValidationError> Validate(BuildUpDef[]? buildUps = null, EffectBuildUpDef[]? changes = null,
        ActionEffectDef[]? effects = null)
    {
        effects ??= [Strike()];
        var set = new ConfigSet();
        set.Add(new ConfigTable(typeof(ActionDef), 2, Actions()));
        set.Add(new ConfigTable(typeof(ActionEffectDef), 2, effects));
        set.Add(new ConfigTable(typeof(HitShapeDef), 1, effects.Where(e => e.TakesShapes()).Select(e => Circle(e.Id)).ToArray()));
        set.Add(new ConfigTable(typeof(StatusEffectDef), 1, new[] { BurnStatus() }));
        set.Add(new ConfigTable(typeof(StatusModifierDef), 1, Array.Empty<StatusModifierDef>()));
        set.Add(new ConfigTable(typeof(BuffIconRegistry), 1, new[] { new BuffIconRegistry { Id = "burn", SortOrder = 1 } }));
        set.Add(new ConfigTable(typeof(EntityVisualDef), 1, new[] { new EntityVisualDef { Id = "fire", PrefabAddress = "Vfx/Fire", ScalePercent = 100 } }));
        set.Add(new ConfigTable(typeof(BuildUpDef), 1, buildUps ?? [Burn()]));
        set.Add(new ConfigTable(typeof(EffectBuildUpDef), 1, changes ?? [Change("skill.strike")]));
        return TableValidator.Validate(set).ToList();
    }

    private static ValidationError SingleError(List<ValidationError> errors, string table, string field)
    {
        var error = Assert.Single(errors);
        Assert.Equal((table, field), (error.Table, error.Field));
        return error;
    }

    [Fact]
    public void The_sample_build_up_is_valid() => Assert.Empty(Validate());

    [Fact]
    public void A_build_up_has_one_of_the_ten_elements_and_one_build_up_per_element()
    {
        SingleError(Validate(buildUps: [Burn(element: Element.None)]), "BuildUpDef", nameof(BuildUpDef.Element));
        var errors = Validate(buildUps: [Burn(), Burn("burn2", netId: 2)]);
        Assert.Equal(2, errors.Count); // each row names the other (R-3.8-10)
        Assert.All(errors, e => Assert.Equal(nameof(BuildUpDef.Element), e.Field));
        Assert.Empty(Validate(buildUps: [Burn(), Burn("cold", netId: 2, element: Element.Ice)]));
    }

    [Fact]
    public void Net_ids_are_unique_and_fit_four_bits()
    {
        Assert.Equal(2, Validate(buildUps: [Burn(), Burn("cold", netId: 1, element: Element.Ice)]).Count);
        SingleError(Validate(buildUps: [Burn(netId: 16)]), "BuildUpDef", nameof(BuildUpDef.NetId));
    }

    [Fact]
    public void A_build_up_names_an_existing_status()
    {
        var burn = Burn();
        burn.StatusId = "frozen";
        SingleError(Validate(buildUps: [burn]), "BuildUpDef", nameof(BuildUpDef.StatusId));
    }

    [Fact]
    public void Resistance_caps_are_ordered()
    {
        var burn = Burn();
        burn.ResistMinPercent = 80;
        SingleError(Validate(buildUps: [burn]), "BuildUpDef", nameof(BuildUpDef.ResistMinPercent));
    }

    [Theory]
    [InlineData(new int[0])]
    [InlineData(new[] { 50, 40, 30, 20, 10 })]
    [InlineData(new[] { 50, 60 })]
    [InlineData(new[] { 101 })]
    [InlineData(new[] { -1 })]
    public void Penalty_levels_are_one_to_four_never_rising_percents(int[] levels)
    {
        var burn = Burn();
        burn.RepeatPenaltyGainPercents = levels;
        SingleError(Validate(buildUps: [burn]), "BuildUpDef", nameof(BuildUpDef.RepeatPenaltyGainPercents));
    }

    [Fact]
    public void A_change_changes_something_once_per_build_up()
    {
        SingleError(Validate(changes: [Change("skill.strike", amount: 0)]), "EffectBuildUpDef", nameof(EffectBuildUpDef.Amount));
        Assert.Empty(Validate(changes: [Change("skill.strike", amount: -10)])); // a reduction (R-3.8-04)
        var errors = Validate(changes: [Change("skill.strike"), Change("skill.strike", id: "again")]);
        Assert.Equal(2, errors.Count);
        Assert.All(errors, e => Assert.Equal(nameof(EffectBuildUpDef.BuildUpId), e.Field));
    }

    [Fact]
    public void At_most_four_changes_per_effect()
    {
        var elements = new[] { Element.Fire, Element.Ice, Element.Lightning, Element.Blood, Element.Poison };
        var buildUps = elements.Select((e, i) => Burn("b" + i, netId: i + 1, element: e)).ToArray();
        var changes = buildUps.Select(b => Change("skill.strike", buildUp: b.Id)).ToArray();
        var errors = Validate(buildUps, changes);
        Assert.Equal(5, errors.Count);
        Assert.All(errors, e => Assert.Equal(nameof(EffectBuildUpDef.EffectId), e.Field));
    }

    [Fact]
    public void Only_effects_that_hit_enemies_change_build_ups()
    {
        Assert.Empty(Validate(changes: [Change("skill.area")], effects: [Area(EffectTarget.Enemies)]));
        SingleError(Validate(changes: [Change("skill.area")], effects: [Area(EffectTarget.Allies)]),
            "EffectBuildUpDef", nameof(EffectBuildUpDef.EffectId));
        SingleError(Validate(changes: [Change("skill.cleanse")], effects: [Cleanse(40)]),
            "EffectBuildUpDef", nameof(EffectBuildUpDef.EffectId));
    }

    [Fact]
    public void A_ground_area_deals_damage_to_enemies_and_heals_allies_per_tick()
    {
        Assert.Empty(Validate(changes: [], effects: [Area(EffectTarget.Enemies)]));
        Assert.Empty(Validate(changes: [], effects: [Area(EffectTarget.Self, EffectTarget.Allies)]));
        Assert.Empty(Validate(changes: [], effects: [Area(EffectTarget.Allies, EffectTarget.Enemies)]));

        var noDamage = Area(EffectTarget.Enemies);
        noDamage.DamagePercent = 0;
        SingleError(Validate(changes: [], effects: [noDamage]), "ActionEffectDef", nameof(ActionEffectDef.DamagePercent));
        var noHeal = Area(EffectTarget.Allies);
        noHeal.HealPercent = 0;
        SingleError(Validate(changes: [], effects: [noHeal]), "ActionEffectDef", nameof(ActionEffectDef.HealPercent));
        var damageOnAllies = Area(EffectTarget.Allies);
        damageOnAllies.DamagePercent = 10;
        SingleError(Validate(changes: [], effects: [damageOnAllies]), "ActionEffectDef", nameof(ActionEffectDef.DamagePercent));
        var healOnEnemies = Area(EffectTarget.Enemies);
        healOnEnemies.HealPercent = 10;
        SingleError(Validate(changes: [], effects: [healOnEnemies]), "ActionEffectDef", nameof(ActionEffectDef.HealPercent));
    }

    [Fact]
    public void A_remove_status_may_only_lower_build_ups_and_other_kinds_may_not()
    {
        Assert.Empty(Validate(changes: [], effects: [Cleanse(40)])); // Simple Cleanse (R-3.8-04)
        Assert.Empty(Validate(changes: [], effects: [Cleanse(40, StatusEffectKind.Debuff)]));
        SingleError(Validate(changes: [], effects: [Cleanse(0)]), "ActionEffectDef", nameof(ActionEffectDef.StatusId));
        SingleError(Validate(changes: [], effects: [Cleanse(101)]), "ActionEffectDef", nameof(ActionEffectDef.BuildUpReduction));

        var strike = Strike();
        strike.BuildUpReduction = 10;
        SingleError(Validate(changes: [], effects: [strike]), "ActionEffectDef", nameof(ActionEffectDef.BuildUpReduction));
        var heal = new ActionEffectDef
        {
            Id = "skill.heal", ActionId = "skill", Kind = EffectKind.Heal, Phase = ActionPhase.Active, Targets = [EffectTarget.Self],
            HealPercent = 100, BuildUpReduction = 20,
        };
        Assert.Empty(Validate(changes: [], effects: [heal])); // healing skills can remove part of the meters (R-3.8-04)
    }

    [Fact]
    public void Only_a_ground_area_names_a_visual_and_it_must_exist()
    {
        var area = Area(EffectTarget.Enemies);
        area.VisualId = "fire";
        Assert.Empty(Validate(changes: [], effects: [area]));
        area.VisualId = "smoke";
        SingleError(Validate(changes: [], effects: [area]), "ActionEffectDef", nameof(ActionEffectDef.VisualId));
        var strike = Strike();
        strike.VisualId = "fire";
        SingleError(Validate(changes: [], effects: [strike]), "ActionEffectDef", nameof(ActionEffectDef.VisualId));
    }
}
