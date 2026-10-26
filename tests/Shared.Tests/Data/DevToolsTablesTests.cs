using System.Text;
using Shared.Data;
using Shared.Data.Tables;

namespace Shared.Tests.Data;

/// <summary>
/// The dev tools' tables (spec 11.7): <see cref="DevTools"/> and its <see cref="Hotkeys"/> rows, the reload class of every
/// table (<see cref="TableReloadPolicy"/>), and the reload hash the server and the dev client share (<see cref="TableHash"/>).
/// </summary>
public class DevToolsTablesTests
{
    private static DevTools Row(string color = "#FF7043FF") => new()
    {
        HotReloadDebounceMs = 300, StrikeTrace = true, TelemetryPollMs = 100, TickWindowMs = 10_000, TimelineTicks = 60,
        GizmoHoldMs = 600, PredictedColor = color, ServerColor = "#FF7043", ServerHitColor = "#FFEB3BFF",
        AreaColor = "#AB47BCFF", ProjectileColor = "#66BB6AFF", BodyColor = "#FFFFFF80",
    };

    private static ConfigSet Set(DevTools row, params string[] hotkeyIds)
    {
        var set = new ConfigSet();
        set.Add(new ConfigTable(typeof(DevTools), 1, new object[] { row }));
        set.Add(new ConfigTable(typeof(Hotkeys), 1,
            hotkeyIds.Select(id => (object)new Hotkeys { Id = id, Binding = "<Keyboard>/f9" }).ToList()));
        return set;
    }

    private static List<string> Errors(ConfigSet set) =>
        TableValidator.Validate(set).Select(e => e.Table + "." + e.Field + ": " + e.Message).ToList();

    [Fact]
    public void A_complete_row_with_its_keys_passes() => Assert.Empty(Errors(Set(Row(), DevTools.Keys)));

    [Fact]
    public void A_color_that_is_not_hex_is_reported()
    {
        var errors = Errors(Set(Row("orange"), DevTools.Keys));

        Assert.Contains(errors, e => e.StartsWith("DevTools.PredictedColor") || e.Contains("'orange'"));
    }

    [Fact]
    public void Every_dev_view_needs_its_hotkey_row()
    {
        var errors = Errors(Set(Row(), Hotkeys.DevHitboxes, Hotkeys.DevTimeline, Hotkeys.DevTickTiming));

        Assert.Contains(errors, e => e.Contains(Hotkeys.DevWorldGeometry));
    }

    [Fact]
    public void Every_table_has_a_reload_class()
    {
        foreach (var type in ConfigTables.All)
            Assert.True(TableReloadPolicy.IsClassified(type), $"{type.Name} has no reload class in TableReloadPolicy");
    }

    [Fact]
    public void Only_hot_tables_have_fixed_rows_and_the_hot_list_keeps_the_table_order()
    {
        foreach (var type in ConfigTables.All.Where(TableReloadPolicy.FixedRows))
            Assert.Equal(ReloadClass.Hot, TableReloadPolicy.ClassOf(type));
        var hot = TableReloadPolicy.HotTables();
        Assert.Equal(ConfigTables.All.Where(t => TableReloadPolicy.ClassOf(t) == ReloadClass.Hot), hot);
        Assert.Contains(typeof(ActionDef), hot);
        Assert.Contains(typeof(HitShapeDef), hot);
        Assert.DoesNotContain(typeof(StartingValuesM1), hot);
    }

    [Fact]
    public void The_hash_follows_every_byte_and_ignores_missing_tables_order_nowhere()
    {
        var files = new Dictionary<string, byte[]>
        {
            ["ActionDef"] = Encoding.UTF8.GetBytes("{ \"a\": 1 }"),
            ["HitShapeDef"] = Encoding.UTF8.GetBytes("{ \"b\": 2 }"),
        };
        var types = new[] { typeof(ActionDef), typeof(HitShapeDef) };
        ulong hash = TableHash.Compute(types, name => files.GetValueOrDefault(name));

        Assert.Equal(hash, TableHash.Compute(types, name => files.GetValueOrDefault(name)));
        Assert.NotEqual(hash, TableHash.Compute(types.Reverse().ToArray(), name => files.GetValueOrDefault(name)));
        files["HitShapeDef"] = Encoding.UTF8.GetBytes("{ \"b\": 3 }");
        Assert.NotEqual(hash, TableHash.Compute(types, name => files.GetValueOrDefault(name)));
        // The bytes of two tables cannot run into each other: a separator follows every name and every file.
        files["ActionDef"] = Encoding.UTF8.GetBytes("{ \"a\": 1 }{");
        files["HitShapeDef"] = Encoding.UTF8.GetBytes(" \"b\": 2 }");
        Assert.NotEqual(hash, TableHash.Compute(types, name => files.GetValueOrDefault(name)));
    }
}
