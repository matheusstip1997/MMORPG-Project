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
        var catalog = Shared.Simulation.Actions.ActionCatalog.From(ConfigLoader.LoadAndValidate(TablesDirectory(), ConfigTables.All).Set);

        // Spec 12.7: dodge 3 charges, 1 every 4 s, 0.25 s of i-frames, 20 stamina; parry 3, 1 every 5 s; attack 10.
        var dodge = catalog[catalog.DodgeIndex];
        var dodgeMeter = catalog.NewMeter(ChargePool.Dodge);
        var parryMeter = catalog.NewMeter(ChargePool.Parry);
        Assert.Equal((3, 80), (dodgeMeter.Max, dodgeMeter.RechargeTicks));
        Assert.Equal((3, 100), (parryMeter.Max, parryMeter.RechargeTicks));
        Assert.Equal((ChargePool.Dodge, 20, 5), (dodge.ChargePool, dodge.StaminaCost, dodge.IFramesEnd - dodge.IFramesStart));
        Assert.Equal(ChargePool.Parry, catalog[catalog.ParryIndex].ChargePool);
        Assert.Equal(10, catalog[catalog.AttackIndex].StaminaCost);
        for (int slot = 1; slot <= ActionBindings.MaxSkillSlots; slot++)
        {
            if (catalog.SlotIndex(slot) >= 0)
                Assert.InRange(catalog[catalog.SlotIndex(slot)].StaminaCost, 15, 30);
        }
    }

    [Fact]
    public void Every_initial_class_has_a_visual()
    {
        // Interim binding (EntityVisualDef): a player's visual id is its class id until ClassDef has a visualId (TODO(E)).
        var set = ConfigLoader.LoadAndValidate(TablesDirectory(), ConfigTables.All).Set;
        Assert.True(set.TryGet(typeof(EntityVisualDef), out var visuals));
        var ids = visuals.Rows.Cast<EntityVisualDef>().Select(v => v.Id).ToHashSet();

        foreach (var classId in set.Single<CharacterCreationConfig>().AllowedInitialClasses)
            Assert.Contains(classId, ids);
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

        var basic = HitShape.From(Assert.Single(rows, r => r.EffectId == "attack.basic.strike"));
        var caster = FixVec2.FromCm(500, 500);
        Assert.True(basic.Overlaps(caster, 0, FixVec2.FromCm(500, 600), FixedPoint.FromCm(40)));   // 1 m ahead (facing 0 is +Y)
        Assert.False(basic.Overlaps(caster, 0, FixVec2.FromCm(500, 400), FixedPoint.FromCm(40)));  // 1 m behind
        foreach (var row in rows)
            HitShape.From(row);
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
