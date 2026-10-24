using Shared.Data;
using Shared.Data.Tables;
using static Shared.Tests.Simulation.ActionTestKit;

namespace Shared.Tests.Data;

/// <summary>Validator rules of ProjectileDef (backlog D3, spec 3.2) and the projectile rules it adds to ActionEffectDef.</summary>
public class ProjectileDefTests
{
    private static ProjectileDef Arrow(string id = "arrow", int netId = 1) => new()
    {
        Id = id, NetId = netId, SpeedCmPerSec = 2400, RangeCm = 2000, HitRadiusCm = 15, MaxHits = 1, StopsOnWorld = true,
        FalloffPercentPerHit = 0, VisualId = "arrow",
    };

    private static ActionEffectDef Shot(string projectileId = "arrow") => new()
    {
        Id = "skill.shot", ActionId = "skill", Kind = EffectKind.Projectile, Phase = ActionPhase.Active,
        PoiseDamage = 10, FlinchPower = 10, FlinchMs = 300, DamagePercent = 100, DamageKind = EffectDamageKind.Physical,
        ProjectileId = projectileId,
    };

    private static List<ValidationError> Validate(ProjectileDef[]? projectiles = null, ActionEffectDef[]? effects = null,
        int tickRateHz = 20)
    {
        var set = new ConfigSet();
        set.Add(new ConfigTable(typeof(ActionDef), 2, new[] { Action("skill", 101, ActionKind.Skill, 300, 100, 300) }));
        set.Add(new ConfigTable(typeof(ActionEffectDef), 2, effects ?? new[] { Shot() }));
        set.Add(new ConfigTable(typeof(ProjectileDef), 1, projectiles ?? new[] { Arrow() }));
        set.Add(new ConfigTable(typeof(EntityVisualDef), 1, new[]
        {
            new EntityVisualDef { Id = "arrow", PrefabAddress = "Visuals/Projectiles/Arrow", ScalePercent = 100 },
        }));
        set.Add(new ConfigTable(typeof(StartingValuesM1), 1, new[] { new StartingValuesM1 { TickRateHz = tickRateHz } }));
        // Only the rows under test: the bare StartingValuesM1 row breaks its own ranges.
        return TableValidator.Validate(set).Where(e => e.Table is "ProjectileDef" or "ActionEffectDef").ToList();
    }

    private static ValidationError SingleError(List<ValidationError> errors, string table, string field)
    {
        var error = Assert.Single(errors);
        Assert.Equal((table, field), (error.Table, error.Field));
        return error;
    }

    [Fact]
    public void The_sample_projectile_and_effect_are_valid() => Assert.Empty(Validate());

    [Fact]
    public void One_action_spawns_no_more_projectiles_than_the_per_owner_cap()
    {
        // Spec 3.2 (capped per owner) and backlog E: Multi Shot's 3 arrows must fit ProjectileSettings.maxPerOwner.
        ActionEffectDef ShotAt(int i, int offsetMs)
        {
            var shot = Shot();
            shot.Id = "skill.shot" + i;
            shot.OffsetMs = offsetMs;
            return shot;
        }
        var three = new[] { ShotAt(1, 0), ShotAt(2, 30), ShotAt(3, 60) };
        List<ValidationError> WithCap(int cap)
        {
            var set = new ConfigSet();
            set.Add(new ConfigTable(typeof(ActionDef), 2, new[] { Action("skill", 101, ActionKind.Skill, 300, 100, 300) }));
            set.Add(new ConfigTable(typeof(ActionEffectDef), 2, three));
            set.Add(new ConfigTable(typeof(ProjectileSettings), 1, new[] { new ProjectileSettings { MaxPerOwner = cap, MaxActive = 64, EventRepeatTicks = 3 } }));
            return TableValidator.Validate(set).Where(e => e.Table == "ActionDef").ToList();
        }

        Assert.Empty(WithCap(3));
        var error = SingleError(WithCap(2), "ActionDef", nameof(ActionDef.Id));
        Assert.Contains("3 projectiles", error.Message);
    }

    [Fact]
    public void A_projectile_effect_must_name_a_loaded_projectile()
    {
        SingleError(Validate(effects: new[] { Shot("") }), "ActionEffectDef", nameof(ActionEffectDef.ProjectileId));
        var broken = SingleError(Validate(effects: new[] { Shot("bolt") }), "ActionEffectDef", nameof(ActionEffectDef.ProjectileId));
        Assert.Equal(ValidationCodes.BrokenRef, broken.Code);
    }

    [Theory]
    [InlineData(nameof(ActionEffectDef.PoiseDamage))]
    [InlineData(nameof(ActionEffectDef.FlinchPower))]
    [InlineData(nameof(ActionEffectDef.FlinchMs))]
    public void Every_projectile_hit_carries_poise_and_flinch(string field)
    {
        // R-3.1-08 and R-3.3-07, through the normal hit rules of R-3.2-02.
        var shot = Shot();
        typeof(ActionEffectDef).GetProperty(field)!.SetValue(shot, 0);
        SingleError(Validate(effects: new[] { shot }), "ActionEffectDef", field);
    }

    [Fact]
    public void Only_projectile_effects_name_a_projectile()
    {
        var strike = Effect("skill.strike", "skill", EffectKind.Strike, ActionPhase.Active, 0);
        strike.ProjectileId = "arrow";
        var errors = Validate(effects: new[] { Shot(), strike });
        Assert.Contains(errors, e => e.Field == nameof(ActionEffectDef.ProjectileId) && e.Message.Contains("must be empty"));
    }

    [Fact]
    public void A_projectile_effect_takes_no_dash_or_area_parameters()
    {
        var shot = Shot();
        shot.DistanceCm = 100;
        SingleError(Validate(effects: new[] { shot }), "ActionEffectDef", nameof(ActionEffectDef.DistanceCm));
    }

    [Fact]
    public void Net_ids_are_unique()
    {
        var errors = Validate(new[] { Arrow(), Arrow("bolt") });
        Assert.Equal(2, errors.Count(e => e.Field == nameof(ProjectileDef.NetId)));
    }

    [Theory]
    [InlineData(nameof(ProjectileDef.NetId), 0)]
    [InlineData(nameof(ProjectileDef.NetId), ProjectileDef.MaxNetId + 1)]
    [InlineData(nameof(ProjectileDef.SpeedCmPerSec), 0)]
    [InlineData(nameof(ProjectileDef.RangeCm), 0)]
    [InlineData(nameof(ProjectileDef.HitRadiusCm), 0)]
    [InlineData(nameof(ProjectileDef.MaxHits), 0)]
    [InlineData(nameof(ProjectileDef.FalloffPercentPerHit), 101)]
    public void Fields_out_of_range_are_refused(string field, int value)
    {
        var arrow = Arrow();
        typeof(ProjectileDef).GetProperty(field)!.SetValue(arrow, value);
        Assert.Contains(Validate(new[] { arrow }), e => e.Code == ValidationCodes.OutOfRange && e.Field == field);
    }

    [Fact]
    public void Every_hit_up_to_max_hits_still_deals_damage()
    {
        var pierce = Arrow();
        pierce.MaxHits = 3;
        pierce.FalloffPercentPerHit = 40; // 100, 60, 20
        Assert.Empty(Validate(new[] { pierce }));
        Assert.Equal(new[] { 100, 60, 20 }, Enumerable.Range(0, 3).Select(pierce.DamagePercentOfHit));

        pierce.FalloffPercentPerHit = 50; // 100, 50, 0
        SingleError(Validate(new[] { pierce }), "ProjectileDef", nameof(ProjectileDef.FalloffPercentPerHit));
    }

    [Fact]
    public void A_tick_step_must_fit_the_sweep()
    {
        var fast = Arrow();
        fast.SpeedCmPerSec = ProjectileDef.MaxStepCmPerTick * 20; // exactly the limit at 20 Hz
        Assert.Empty(Validate(new[] { fast }));
        fast.SpeedCmPerSec++;
        SingleError(Validate(new[] { fast }), "ProjectileDef", nameof(ProjectileDef.SpeedCmPerSec));
        Assert.Empty(Validate(new[] { fast }, tickRateHz: 30));
    }

    [Fact]
    public void The_visual_must_exist()
    {
        var arrow = Arrow();
        arrow.VisualId = "bolt";
        var error = SingleError(Validate(new[] { arrow }), "ProjectileDef", nameof(ProjectileDef.VisualId));
        Assert.Equal(ValidationCodes.BrokenRef, error.Code);
    }
}
