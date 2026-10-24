using GameServer.Host.Config;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Simulation;
using Shared.Simulation.Combat;
using Shared.World;

namespace GameServer.Tests.Host.Config;

/// <summary>The tables committed in data/tables must always be valid.</summary>
public class RepositoryDataTests
{
    private static string TablesDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Mmorpg.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "data", "tables");
    }

    [Fact]
    public void Committed_tables_are_valid()
    {
        var result = ConfigLoader.LoadAndValidate(TablesDirectory(), ConfigTables.All);

        Assert.True(result.Ok, string.Join("\n", result.Errors));
    }

    [Fact]
    public void StartingValuesM1_matches_spec_12_7_tick_rate()
    {
        var values = ConfigLoader.LoadAndValidate(TablesDirectory(), ConfigTables.All).Set.Single<StartingValuesM1>();

        // Architecture rule 12: 20 Hz tick, lag compensation capped at 200 ms.
        Assert.Equal(20, values.TickRateHz);
        Assert.Equal(200, values.LagCompensationCapMs);
    }

    [Fact]
    public void The_action_catalog_has_the_spec_12_7_costs_charges_and_i_frames()
    {
        var set = ConfigLoader.LoadAndValidate(TablesDirectory(), ConfigTables.All).Set;
        var catalog = Shared.Simulation.Actions.ActionCatalog.From(set);
        var dodgeMeter = catalog.NewMeter(ChargePool.Dodge);
        var parryMeter = catalog.NewMeter(ChargePool.Parry);
        Assert.Equal((3, 80), (dodgeMeter.Max, dodgeMeter.RechargeTicks));
        Assert.Equal((3, 100), (parryMeter.Max, parryMeter.RechargeTicks));

        // Every class's row (backlog E). Spec 12.7: dodge 3 charges, 1 every 4 s, 0.25 s of i-frames, 20 stamina; parry 3,
        // 1 every 5 s; normal attack 10 stamina, skills 15-30; R-3.1-04: MP for skills only, within the class's pool.
        Assert.True(set.TryGet(typeof(ClassDef), out var classes));
        foreach (ClassDef classDef in classes.Rows)
        {
            var loadout = catalog.Loadout(classDef.Id);
            Assert.Equal(classDef.Id, loadout.ClassId);
            var dodge = catalog[loadout.DodgeIndex];
            Assert.Equal((ChargePool.Dodge, 20, 5), (dodge.ChargePool, dodge.StaminaCost, dodge.IFramesEnd - dodge.IFramesStart));
            Assert.Equal(ChargePool.Parry, catalog[loadout.ParryIndex].ChargePool);
            Assert.Equal((10, 0), (catalog[loadout.AttackIndex].StaminaCost, catalog[loadout.AttackIndex].MpCost));
            for (int slot = 1; slot <= ActionBindings.MaxSkillSlots; slot++)
            {
                if (loadout.SlotIndex(slot) < 0)
                    continue;
                var skill = catalog[loadout.SlotIndex(slot)];
                Assert.InRange(skill.StaminaCost, 15, 30);
                Assert.InRange(skill.MpCost, 1, classDef.MaxMp);
            }
        }
    }

    [Fact]
    public void The_warrior_and_archer_kits_are_on_Q_E_R_F_and_1()
    {
        // Backlog E (docs/design/skills-m1-proposal.md): slot 1 is key 1, slots 7-10 are Q E R F (GameInput).
        var set = ConfigLoader.LoadAndValidate(TablesDirectory(), ConfigTables.All).Set;
        var catalog = Shared.Simulation.Actions.ActionCatalog.From(set);
        string?[] Kit(string classId)
        {
            var loadout = catalog.Loadout(classId);
            return new[] { 1, 7, 8, 9, 10 }.Select(slot => loadout.SlotIndex(slot) < 0 ? null : catalog[loadout.SlotIndex(slot)].Id).ToArray();
        }

        Assert.Equal("attack.warrior", catalog[catalog.Loadout("warrior").AttackIndex].Id);
        Assert.Equal(new string?[] { null, "skill.warrior.slash", "skill.warrior.headCut", "skill.warrior.piercingAttack", "skill.warrior.jumpStrike" },
            Kit("warrior")); // the Stamina buff (slot 1) comes with the statuses (wave 5)
        Assert.Equal("attack.archer", catalog[catalog.Loadout("archer").AttackIndex].Id);
        Assert.Equal(new string?[] { "skill.archer.dash", "skill.archer.piercingShot", "skill.archer.multiShot", "skill.archer.heavyShot", "skill.archer.backStep" },
            Kit("archer"));
        Assert.Equal("attack.mage", catalog[catalog.Loadout("mage").AttackIndex].Id);
        Assert.Equal("attack.assist", catalog[catalog.Loadout("assist").AttackIndex].Id);
        // No parry colors on player skills in M1 (PvP is off).
        foreach (var id in Kit("warrior").Concat(Kit("archer")).OfType<string>())
            Assert.False(catalog.Find(id)!.Def.HasParryColor(), id);
    }

    [Fact]
    public void Every_initial_class_is_a_ClassDef_row_with_a_visual()
    {
        var set = ConfigLoader.LoadAndValidate(TablesDirectory(), ConfigTables.All).Set;
        Assert.True(set.TryGet(typeof(EntityVisualDef), out var visuals));
        var ids = visuals.Rows.Cast<EntityVisualDef>().Select(v => v.Id).ToHashSet();

        foreach (var classId in set.Single<CharacterCreationConfig>().AllowedInitialClasses)
        {
            var classDef = ClassDef.Find(set, classId);
            Assert.NotNull(classDef);
            Assert.Contains(classDef.VisualId, ids);
        }
    }

    [Fact]
    public void The_committed_world_has_every_marker_the_tables_name()
    {
        // Spec 11.6: marker ids are checked against the config tables (player spawn, SpawnPoints).
        var set = ConfigLoader.LoadAndValidate(TablesDirectory(), ConfigTables.All).Set;
        var world = GameServer.World.Map.WorldLoader.Load(Path.Combine(TablesDirectory(), "..", "world", "m1"), set);

        Assert.True(world.Ok, string.Join("\n", world.Errors));
        Assert.Empty(GameServer.World.Map.WorldLoader.CheckConfigMarkers(world.Map!, set));
        Assert.True(set.TryGet(typeof(SpawnPoints), out var points));
        Assert.NotEmpty(points.Rows);
    }

    [Fact]
    public void Committed_hit_shapes_build_and_every_strike_has_one()
    {
        // R-3.1-03 (backlog D3): the validator already demands a shape per strike; check the loaded rows build and face forward.
        var set = ConfigLoader.LoadAndValidate(TablesDirectory(), ConfigTables.All).Set;
        Assert.True(set.TryGet(typeof(HitShapeDef), out var shapes));
        Assert.True(set.TryGet(typeof(ActionEffectDef), out var effects));
        var rows = shapes.Rows.Cast<HitShapeDef>().ToList();

        foreach (var strike in effects.Rows.Cast<ActionEffectDef>().Where(e => e.Kind == EffectKind.Strike))
            Assert.Contains(rows, r => r.EffectId == strike.Id);

        var basic = HitShape.From(Assert.Single(rows, r => r.EffectId == "attack.warrior.strike"));
        var caster = FixVec2.FromCm(500, 500);
        Assert.True(basic.Overlaps(caster, 0, FixVec2.FromCm(500, 600), FixedPoint.FromCm(40)));   // 1 m ahead (facing 0 is +Y)
        Assert.False(basic.Overlaps(caster, 0, FixVec2.FromCm(500, 400), FixedPoint.FromCm(40)));  // 1 m behind
        foreach (var row in rows)
            HitShape.From(row);
    }

    [Fact]
    public void Committed_projectiles_load_and_arrow_shot_fires_the_arrow()
    {
        // Spec 3.2 (backlog D3): every projectile effect names a ProjectileDef; Arrow Shot's arrow stops at the first target.
        var set = ConfigLoader.LoadAndValidate(TablesDirectory(), ConfigTables.All).Set;
        var rules = GameServer.Combat.ProjectileRules.From(set);
        Assert.True(set.TryGet(typeof(ActionEffectDef), out var effects));
        foreach (var effect in effects.Rows.Cast<ActionEffectDef>().Where(e => e.Kind == EffectKind.Projectile))
            Assert.True(rules.TryGet(effect.ProjectileId, out _), effect.Id);

        var shot = Assert.Single(effects.Rows.Cast<ActionEffectDef>(), e => e.Id == "skill.arrowShot.projectile");
        Assert.True(rules.TryGet(shot.ProjectileId, out var arrow));
        Assert.Equal((1, true), (arrow.MaxHits, arrow.StopsOnWorld));
        Assert.True(shot.PoiseDamage > 0 && shot.FlinchPower > 0 && shot.FlinchMs > 0); // R-3.1-08
        Assert.True(rules.Settings.MaxPerOwner > 0 && rules.Settings.MaxActive >= rules.Settings.MaxPerOwner);
    }

    [Theory]
    // Spec 2.3 "Default flags by area type": PvP, honor effects, drops.
    [InlineData("normalCity", PvpMode.Off, true, DeathDrop.Off)]
    [InlineData("lawlessCity", PvpMode.AlwaysOn, false, DeathDrop.On)]
    [InlineData("openWorld", PvpMode.ArmedOnly, true, DeathDrop.On)]
    [InlineData("regularDungeon", PvpMode.Off, false, DeathDrop.Off)]
    [InlineData("raid", PvpMode.Off, false, DeathDrop.Off)]
    [InlineData("arena", PvpMode.AlwaysOn, false, DeathDrop.Off)]
    [InlineData("siege", PvpMode.AlwaysOn, false, DeathDrop.Off)]
    public void RegionDefaults_match_the_spec_2_3_table(string areaType, PvpMode pvp, bool honorEffects, DeathDrop drops)
    {
        var set = ConfigLoader.LoadAndValidate(TablesDirectory(), ConfigTables.All).Set;
        Assert.True(set.TryGet(typeof(RegionDefaults), out var table));

        var row = Assert.Single(table.Rows.Cast<RegionDefaults>(), r => r.Id == areaType);

        Assert.Equal(new RegionFlags(pvp, honorEffects, drops), row.ToFlags());
    }
}
