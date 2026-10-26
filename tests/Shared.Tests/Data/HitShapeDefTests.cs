using Shared.Data;
using Shared.Data.Tables;
using static Shared.Tests.Simulation.ActionTestKit;

namespace Shared.Tests.Data;

/// <summary>Validator rules of HitShapeDef (backlog D3, R-3.1-03) and the shape rule it adds to ActionEffectDef.</summary>
public class HitShapeDefTests
{
    private static ActionDef[] Actions() =>
    [
        Action("attack", 1, ActionKind.Attack, 300, 100, 300),
        Action("skill", 101, ActionKind.Skill, 200, 100, 300, channel: 1000),
    ];

    private static ActionEffectDef[] Effects() =>
    [
        Effect("attack.strike", "attack", EffectKind.Strike, ActionPhase.Active, 0),
        Effect("skill.dash", "skill", EffectKind.Dash, ActionPhase.Startup, 0, distanceCm: 300, durationMs: 200),
        new ActionEffectDef
        {
            Id = "skill.area", ActionId = "skill", Kind = EffectKind.GroundArea, Phase = ActionPhase.Active, Targets = [EffectTarget.Enemies],
            DurationMs = 3000, TickIntervalMs = 500, PoiseDamage = 5, DamagePercent = 40, DamageKind = EffectDamageKind.Magic,
        },
    ];

    private static HitShapeDef Cone(string id = "attack.strike.cone", string effectId = "attack.strike") => new()
    {
        Id = id, EffectId = effectId, Kind = HitShapeKind.Cone, RangeCm = 200, HalfAngleDeg = 60,
    };

    private static HitShapeDef[] Shapes() =>
    [
        Cone(),
        new HitShapeDef { Id = "attack.strike.box", EffectId = "attack.strike", Kind = HitShapeKind.Box, ForwardOffsetCm = 50, LengthCm = 100, WidthCm = 80 },
        new HitShapeDef { Id = "skill.area.circle", EffectId = "skill.area", Kind = HitShapeKind.Capsule, ForwardOffsetCm = 300, RadiusCm = 300 },
    ];

    private static List<ValidationError> Validate(HitShapeDef[]? shapes = null, ActionEffectDef[]? effects = null)
    {
        var set = new ConfigSet();
        set.Add(new ConfigTable(typeof(ActionDef), 2, Actions()));
        set.Add(new ConfigTable(typeof(ActionEffectDef), 2, effects ?? Effects()));
        set.Add(new ConfigTable(typeof(HitShapeDef), 1, shapes ?? Shapes()));
        return TableValidator.Validate(set);
    }

    private static ValidationError SingleError(List<ValidationError> errors, string table, string field)
    {
        var error = Assert.Single(errors);
        Assert.Equal((table, field), (error.Table, error.Field));
        return error;
    }

    [Fact]
    public void The_sample_shapes_are_valid() => Assert.Empty(Validate());

    [Fact]
    public void Every_strike_and_ground_area_has_a_shape()
    {
        // R-3.1-03: a strike is resolved from its shapes.
        var error = SingleError(Validate(Shapes()[2..]), "ActionEffectDef", nameof(ActionEffectDef.Id));
        Assert.Equal(0, error.Row);
        Assert.Contains("Strike", error.Message);

        error = SingleError(Validate(Shapes()[..2]), "ActionEffectDef", nameof(ActionEffectDef.Id));
        Assert.Equal(2, error.Row);
    }

    [Fact]
    public void Without_the_shape_table_the_rule_waits_for_it()
    {
        // A set without HitShapeDef (a MissingTable error in a real load) does not report every strike as well.
        var set = new ConfigSet();
        set.Add(new ConfigTable(typeof(ActionDef), 2, Actions()));
        set.Add(new ConfigTable(typeof(ActionEffectDef), 2, Effects()));
        Assert.Empty(TableValidator.Validate(set));
    }

    [Fact]
    public void Only_strikes_and_ground_areas_have_shapes()
    {
        var error = SingleError(Validate([.. Shapes(), Cone("skill.dash.cone", "skill.dash")]), "HitShapeDef", nameof(HitShapeDef.EffectId));
        Assert.Equal(3, error.Row);
        Assert.Contains("Dash", error.Message);
    }

    [Fact]
    public void A_shape_points_to_an_existing_effect()
    {
        var error = SingleError(Validate([.. Shapes(), Cone("x", "nothing")]), "HitShapeDef", nameof(HitShapeDef.EffectId));
        Assert.Equal(ValidationCodes.BrokenRef, error.Code);
    }

    [Fact]
    public void An_effect_has_at_most_four_shapes()
    {
        var shapes = Enumerable.Range(0, HitShapeDef.MaxShapesPerEffect + 1).Select(i => Cone("cone" + i)).Append(Shapes()[2]).ToArray();
        var errors = Validate(shapes);
        Assert.Equal(HitShapeDef.MaxShapesPerEffect + 1, errors.Count);
        Assert.All(errors, e => Assert.Equal((nameof(HitShapeDef), nameof(HitShapeDef.EffectId)), (e.Table, e.Field)));

        Assert.Empty(Validate(shapes.Skip(1).ToArray()));
    }

    [Theory]
    [InlineData(HitShapeKind.Cone, nameof(HitShapeDef.RangeCm))]
    [InlineData(HitShapeKind.Cone, nameof(HitShapeDef.HalfAngleDeg))]
    [InlineData(HitShapeKind.Box, nameof(HitShapeDef.LengthCm))]
    [InlineData(HitShapeKind.Box, nameof(HitShapeDef.WidthCm))]
    [InlineData(HitShapeKind.Capsule, nameof(HitShapeDef.RadiusCm))]
    public void Each_kind_needs_its_sizes(HitShapeKind kind, string field)
    {
        var shape = Full(kind);
        typeof(HitShapeDef).GetProperty(field)!.SetValue(shape, 0);
        SingleError(Validate([.. Shapes(), shape]), "HitShapeDef", field);
    }

    [Theory]
    [InlineData(HitShapeKind.Cone, nameof(HitShapeDef.LengthCm))]
    [InlineData(HitShapeKind.Cone, nameof(HitShapeDef.WidthCm))]
    [InlineData(HitShapeKind.Cone, nameof(HitShapeDef.RadiusCm))]
    [InlineData(HitShapeKind.Box, nameof(HitShapeDef.RangeCm))]
    [InlineData(HitShapeKind.Box, nameof(HitShapeDef.HalfAngleDeg))]
    [InlineData(HitShapeKind.Box, nameof(HitShapeDef.RadiusCm))]
    [InlineData(HitShapeKind.Capsule, nameof(HitShapeDef.RangeCm))]
    [InlineData(HitShapeKind.Capsule, nameof(HitShapeDef.HalfAngleDeg))]
    [InlineData(HitShapeKind.Capsule, nameof(HitShapeDef.WidthCm))]
    public void Sizes_a_kind_does_not_use_must_be_zero(HitShapeKind kind, string field)
    {
        var shape = Full(kind);
        typeof(HitShapeDef).GetProperty(field)!.SetValue(shape, 10);
        SingleError(Validate([.. Shapes(), shape]), "HitShapeDef", field);
    }

    [Fact]
    public void A_capsule_may_have_length_zero_and_the_kind_must_be_known()
    {
        var circle = Full(HitShapeKind.Capsule);
        circle.LengthCm = 0;
        Assert.Empty(Validate([.. Shapes(), circle]));

        var unknown = Full(HitShapeKind.Cone);
        unknown.Kind = 0;
        Assert.Contains(Validate([.. Shapes(), unknown]), e => e.Field == nameof(HitShapeDef.Kind));
    }

    [Theory]
    [InlineData(nameof(HitShapeDef.HalfAngleDeg), 181)]
    [InlineData(nameof(HitShapeDef.RangeCm), 5_001)]
    [InlineData(nameof(HitShapeDef.ForwardOffsetCm), -2_001)]
    [InlineData(nameof(HitShapeDef.SideOffsetCm), 2_001)]
    public void Fields_are_bounded(string field, int value)
    {
        var shape = Full(HitShapeKind.Cone);
        typeof(HitShapeDef).GetProperty(field)!.SetValue(shape, value);
        var error = SingleError(Validate([.. Shapes(), shape]), "HitShapeDef", field);
        Assert.Equal(ValidationCodes.OutOfRange, error.Code);
    }

    /// <summary>A valid shape of the kind on the ground area (which has room for one more).</summary>
    private static HitShapeDef Full(HitShapeKind kind)
    {
        var shape = new HitShapeDef { Id = "extra", EffectId = "skill.area", Kind = kind, SideOffsetCm = -40 };
        switch (kind)
        {
            case HitShapeKind.Cone: shape.RangeCm = 150; shape.HalfAngleDeg = 30; break;
            case HitShapeKind.Box: shape.LengthCm = 150; shape.WidthCm = 50; break;
            default: shape.LengthCm = 150; shape.RadiusCm = 40; break;
        }
        return shape;
    }
}
