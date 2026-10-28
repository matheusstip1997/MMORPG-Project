using Shared.Data;
using Shared.Data.Tables;

namespace Shared.Tests.Data;

/// <summary>
/// The minimap's tables (spec 9.3, 9.12; WP-M2-10b): <see cref="MinimapMarkerDef"/> (R-9.12-01 the source's fields,
/// R-9.12-02 one arrow, one death spot, each region pair once, R-9.12-03 color, size and level), <see cref="MinimapIconRegistry"/>
/// (R-9.12-04 every icon used), <see cref="MinimapRegionTint"/> and <see cref="MinimapSettings"/>.
/// </summary>
public class MinimapTablesTests
{
    private static MinimapMarkerDef Marker(string id, MinimapSource source, string icon = "dot") => new()
    {
        Id = id, Source = source, IconId = icon, Color = "#e0a43a", SizePx = 8, DrawOrder = 10,
        NpcKinds = source == MinimapSource.Npc ? new[] { NpcKind.Outpost } : Array.Empty<NpcKind>(),
        Regions = source == MinimapSource.RegionPass ? new[] { "ring1", "ring2" } : Array.Empty<string>(),
        QuestKinds = source == MinimapSource.RunCamp ? new[] { QuestKind.Kill } : Array.Empty<QuestKind>(),
    };

    private static MinimapMarkerDef Arrow() => Marker("player", MinimapSource.OwnEntity, "arrow");

    private static ConfigSet Set(IEnumerable<MinimapMarkerDef> markers, IEnumerable<MinimapIconRegistry>? icons = null,
        IEnumerable<MinimapRegionTint>? tints = null, MinimapSettings? settings = null)
    {
        var set = new ConfigSet();
        set.Add(new ConfigTable(typeof(RegionDef), 1, new object[]
        {
            new RegionDef { Id = "ring1", AreaType = "openWorld" }, new RegionDef { Id = "ring2", AreaType = "openWorld" },
            new RegionDef { Id = "ring3", AreaType = "openWorld" },
        }));
        set.Add(new ConfigTable(typeof(ProgressionSettings), 1, new object[] { new ProgressionSettings { MaxLevel = 130, SkillPointsPerLevel = 1 } }));
        set.Add(new ConfigTable(typeof(MinimapMarkerDef), 1, markers.Cast<object>().ToList()));
        set.Add(new ConfigTable(typeof(MinimapIconRegistry), 1, (icons ?? new[]
        {
            new MinimapIconRegistry { Id = "arrow", PlaceholderShape = MinimapShape.Arrow },
            new MinimapIconRegistry { Id = "dot", PlaceholderShape = MinimapShape.Circle },
        }).Cast<object>().ToList()));
        set.Add(new ConfigTable(typeof(MinimapRegionTint), 1, (tints ?? new[] { new MinimapRegionTint { Id = "ring1", Color = "#6b8c5c" } })
            .Cast<object>().ToList()));
        set.Add(new ConfigTable(typeof(MinimapSettings), 1, new object[]
        {
            settings ?? new MinimapSettings
            {
                CmPerPx = 100, CellsPerTexel = 2, WalkableColor = "#5f6b5c", BlockedColor = "#1b1f26", CampMergeRadiusCm = 1500,
                DeathSpotMs = 600_000,
            },
        }));
        return set;
    }

    private static List<string> Errors(ConfigSet set) =>
        TableValidator.Validate(set).Where(e => !(e.Code == ValidationCodes.BrokenRef && e.Table == "RegionDef"))
            .Select(e => e.Table + ": " + e.Message).ToList();

    [Fact]
    public void One_row_per_source_with_its_fields_passes()
    {
        var markers = new[]
        {
            Arrow(), Marker("outpost", MinimapSource.Npc), Marker("gate", MinimapSource.RegionPass), Marker("camp", MinimapSource.RunCamp),
            Marker("death", MinimapSource.DeathSpot), Marker("party", MinimapSource.PartyMember),
        };

        Assert.Empty(Errors(Set(markers)));
    }

    [Fact]
    public void R_9_12_01_an_npc_row_needs_its_kinds_and_no_other_row_has_them()
    {
        var npc = Marker("outpost", MinimapSource.Npc);
        npc.NpcKinds = Array.Empty<NpcKind>();
        var death = Marker("death", MinimapSource.DeathSpot);
        death.NpcKinds = new[] { NpcKind.Vendor };

        var errors = Errors(Set(new[] { Arrow(), npc, death }));

        Assert.Contains(errors, e => e.Contains("at least one NPC kind"));
        Assert.Contains(errors, e => e.Contains("only npc rows"));
    }

    [Theory]
    [InlineData("ring1")]
    [InlineData("ring1", "ring1")]
    [InlineData("ring1", "ring2", "ring3")]
    public void R_9_12_01_a_region_pass_names_exactly_two_different_regions(params string[] regions)
    {
        var pass = Marker("gate", MinimapSource.RegionPass);
        pass.Regions = regions;

        Assert.Contains(Errors(Set(new[] { Arrow(), pass })), e => e.Contains("exactly two different regions"));
    }

    [Fact]
    public void R_9_12_01_a_run_camp_lists_kill_or_return_steps_only()
    {
        var camp = Marker("camp", MinimapSource.RunCamp);
        camp.QuestKinds = new[] { QuestKind.Kill, QuestKind.Deliver };
        var none = Marker("camp2", MinimapSource.RunCamp);
        none.QuestKinds = Array.Empty<QuestKind>();

        var errors = Errors(Set(new[] { Arrow(), camp, none }));

        Assert.Contains(errors, e => e.Contains("'Deliver' steps have no camps"));
        Assert.Contains(errors, e => e.Contains("at least one step kind"));
    }

    [Fact]
    public void R_9_12_02_exactly_one_arrow()
    {
        Assert.Contains(Errors(Set(new[] { Marker("outpost", MinimapSource.Npc) })), e => e.Contains("needs one ownEntity row"));
        Assert.Contains(Errors(Set(new[] { Arrow(), Marker("player2", MinimapSource.OwnEntity, "arrow") })),
            e => e.Contains("2 ownEntity rows"));
    }

    [Fact]
    public void R_9_12_02_one_death_spot_and_each_region_pair_once_in_either_order()
    {
        var reversed = Marker("gate2", MinimapSource.RegionPass);
        reversed.Regions = new[] { "ring2", "ring1" };

        var errors = Errors(Set(new[]
        {
            Arrow(), Marker("death", MinimapSource.DeathSpot), Marker("death2", MinimapSource.DeathSpot),
            Marker("gate", MinimapSource.RegionPass), reversed,
        }));

        Assert.Contains(errors, e => e.Contains("2 deathSpot rows"));
        Assert.Contains(errors, e => e.Contains("is marked by 2 rows"));
    }

    [Fact]
    public void R_9_12_03_color_size_and_a_reachable_level()
    {
        var marker = Marker("outpost", MinimapSource.Npc);
        marker.Color = "e0a43a";
        marker.SizePx = 2;
        marker.VisibleFromLevel = 131;

        var errors = Errors(Set(new[] { Arrow(), marker }));

        Assert.Contains(errors, e => e.Contains("'e0a43a' is not #RRGGBB"));
        Assert.Contains(errors, e => e.Contains("2 is outside [4, 32]"));
        Assert.Contains(errors, e => e.Contains("above the level cap 130"));
    }

    [Fact]
    public void R_9_12_04_an_unused_icon_and_a_bad_address_are_refused()
    {
        var icons = new[]
        {
            new MinimapIconRegistry { Id = "arrow", PlaceholderShape = MinimapShape.Arrow, IconAddress = "/Icons/Arrow" },
            new MinimapIconRegistry { Id = "dot", PlaceholderShape = MinimapShape.Circle },
        };

        var errors = Errors(Set(new[] { Arrow() }, icons));

        Assert.Contains(errors, e => e.StartsWith("MinimapIconRegistry") && e.Contains("leading /"));
        Assert.Contains(errors, e => e.StartsWith("MinimapIconRegistry") && e.Contains("'dot' is used by no"));
    }

    [Fact]
    public void Tints_and_settings_need_valid_colors_and_known_regions()
    {
        var errors = Errors(Set(new[] { Arrow() },
            tints: new[] { new MinimapRegionTint { Id = "nowhere", Color = "#zzzzzz" } },
            settings: new MinimapSettings { CmPerPx = 100, CellsPerTexel = 2, WalkableColor = "green", BlockedColor = "#000000", DeathSpotMs = 1000 }));

        Assert.Contains(errors, e => e.StartsWith("MinimapRegionTint") && e.Contains("#zzzzzz"));
        Assert.Contains(errors, e => e.StartsWith("MinimapRegionTint") && e.Contains("nowhere"));
        Assert.Contains(errors, e => e.StartsWith("MinimapSettings") && e.Contains("'green'"));
    }
}
