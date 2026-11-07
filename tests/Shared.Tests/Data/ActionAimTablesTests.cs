using Shared.Data;
using Shared.Data.Tables;

namespace Shared.Tests.Data;

/// <summary>
/// M3 decision 16 (R-2.2-06, spec 3.1 Targeting): <see cref="ActionDef.Aim"/> is data, cross-checked with the action's
/// effects: a move skill (a <c>moveInput</c> dash) aims with the body and only a move skill does; a ground aim needs a
/// <c>groundArea</c> effect; a ground area placed away from the caster aims at the mouse, one cast from the character may
/// aim at the target.
/// </summary>
public class ActionAimTablesTests
{
    private static ActionDef Skill(string id, ActionAim aim) => new()
    {
        Id = id, NetId = 1, Kind = ActionKind.Skill, StartupMs = 100, ActiveMs = 100, RecoveryMs = 100, StaminaCost = 10,
        MpCost = 1, Aim = aim,
    };

    private static ActionEffectDef MoveDash(string actionId) => new()
    {
        Id = actionId + ".dash", ActionId = actionId, Kind = EffectKind.Dash, Phase = ActionPhase.Active, DistanceCm = 400,
        DurationMs = 100, DashDirection = DashDirection.MoveInput, DirectionOffsetDeg = 180,
    };

    private static ActionEffectDef Area(string actionId) => new()
    {
        Id = actionId + ".area", ActionId = actionId, Kind = EffectKind.GroundArea, Phase = ActionPhase.Active,
        DurationMs = 1000, TickIntervalMs = 250, Targets = [EffectTarget.Enemies],
    };

    private static HitShapeDef Shape(string effectId, int forwardCm) => new()
    {
        Id = effectId + ".shape" + forwardCm, EffectId = effectId, Kind = HitShapeKind.Cone, ForwardOffsetCm = forwardCm,
        RangeCm = 300, HalfAngleDeg = 30,
    };

    private static List<string> AimErrors(ActionDef action, ActionEffectDef[] effects, HitShapeDef[]? shapes = null)
    {
        var set = new ConfigSet();
        set.Add(new ConfigTable(typeof(ActionDef), 1, new object[] { action }));
        set.Add(new ConfigTable(typeof(ActionEffectDef), 1, effects.Cast<object>().ToList()));
        set.Add(new ConfigTable(typeof(HitShapeDef), 1, (shapes ?? []).Cast<object>().ToList()));
        var errors = new RowErrors();
        action.Validate(set, errors);
        return errors.Items.Select(e => e.Field + ": " + e.Message).Where(e => e.StartsWith(nameof(ActionDef.Aim))).ToList();
    }

    [Fact]
    public void A_move_skill_aims_with_the_body()
    {
        Assert.Empty(AimErrors(Skill("dash", ActionAim.Body), [MoveDash("dash")]));
        Assert.Contains(AimErrors(Skill("dash", ActionAim.Target), [MoveDash("dash")]), e => e.Contains("aim body"));
        Assert.Contains(AimErrors(Skill("dash", ActionAim.Ground), [MoveDash("dash")]), e => e.Contains("aim body"));
    }

    [Fact]
    public void Only_a_move_skill_aims_with_the_body()
    {
        var facingDash = MoveDash("leap");
        facingDash.DashDirection = DashDirection.Facing; // Jump Strike: a dash along the aim, not a move skill
        Assert.Contains(AimErrors(Skill("leap", ActionAim.Body), [facingDash]), e => e.Contains("needs a moveInput dash"));
        Assert.Empty(AimErrors(Skill("leap", ActionAim.Target), [facingDash]));
    }

    [Fact]
    public void A_ground_aim_needs_a_ground_area()
    {
        Assert.Contains(AimErrors(Skill("buff", ActionAim.Ground), []), e => e.Contains("needs a groundArea"));
        Assert.Empty(AimErrors(Skill("buff", ActionAim.Target), []));
    }

    [Fact]
    public void An_action_with_a_ground_area_writes_its_aim()
    {
        // #154 review: where it lands is a choice in the table, not the shapes' forwardOffsetCm alone.
        var area = Area("flame");
        var unwritten = new ActionDef { Id = "flame", NetId = 9, Kind = ActionKind.Skill, StartupMs = 100, ActiveMs = 100, RecoveryMs = 100 };
        Assert.Contains(AimErrors(unwritten, [area], [Shape(area.Id, 0)]), e => e.Contains("writes its aim"));
        Assert.Empty(AimErrors(Skill("flame", ActionAim.Target), [area], [Shape(area.Id, 0)]));
        Assert.Empty(AimErrors(unwritten, [])); // without a ground area the default stands
    }

    [Fact]
    public void A_ground_area_placed_away_from_the_caster_aims_at_the_mouse()
    {
        // Ice Pike: its capsule 150 cm in front of the caster.
        var area = Area("pike");
        Assert.Empty(AimErrors(Skill("pike", ActionAim.Ground), [area], [Shape(area.Id, 150)]));
        Assert.Contains(AimErrors(Skill("pike", ActionAim.Target), [area], [Shape(area.Id, 150)]), e => e.Contains("aim ground"));
    }

    [Fact]
    public void A_ground_area_cast_from_the_character_may_aim_at_the_target_or_the_mouse()
    {
        // Flamethrower: its cone starts at the caster (one shape at 0 cm), so it follows the lock.
        var area = Area("flame");
        HitShapeDef[] shapes = [Shape(area.Id, 0), Shape(area.Id, 150)];
        Assert.Empty(AimErrors(Skill("flame", ActionAim.Target), [area], shapes));
        Assert.Empty(AimErrors(Skill("flame", ActionAim.Ground), [area], shapes));
    }
}
