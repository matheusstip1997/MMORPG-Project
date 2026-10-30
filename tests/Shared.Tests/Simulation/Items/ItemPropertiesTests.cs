using Shared.Simulation.Items;

namespace Shared.Tests.Simulation.Items;

/// <summary>
/// Typed item properties v2 (spec 4.1 M3 item model, WP-M3-2): v1 in, v2 out, unknown members kept, deterministic text,
/// malformed and later versions refused, the caps.
/// </summary>
public class ItemPropertiesTests
{
    private const string V1 = "{\"v\":1,\"enchant\":0,\"gems\":[],\"originalOwner\":42,\"ownerHistory\":[]}";

    [Fact]
    public void A_v1_row_reads_with_defaults_and_is_written_as_v2()
    {
        Assert.True(ItemProperties.TryParse(V1, out var properties, out string error), error);

        Assert.Equal(1, properties.ReadVersion);
        Assert.Equal((0, 0, 0L, 42L, false, false), (properties.Enchant, properties.Quality, properties.CrafterId,
            properties.OriginalOwner, properties.Tainted, properties.HasCraftedStats));
        Assert.Empty(properties.Gems);
        Assert.Equal(
            "{\"v\":2,\"enchant\":0,\"gems\":[],\"craftedStats\":[],\"quality\":0,\"crafterId\":0,\"originalOwner\":42,\"ownerHistory\":[],\"tainted\":false}",
            properties.ToJson());
    }

    [Fact]
    public void The_bare_m1_default_reads()
    {
        Assert.True(ItemProperties.TryParse("{\"v\":1}", out var properties, out _));
        Assert.Equal(0, properties.OriginalOwner);
    }

    [Fact]
    public void A_v2_crafted_item_round_trips()
    {
        var crafted = ItemProperties.New(7);
        crafted.Enchant = 3;
        crafted.Gems.Add("m3.gemT1");
        crafted.CraftedStats.Add(new ItemStatBonus("atk", 14, 0));
        crafted.CraftedStats.Add(new ItemStatBonus("def", 0, -5));
        crafted.Quality = 812;
        crafted.CrafterId = 9;
        crafted.OwnerHistory.Add(3);
        crafted.Tainted = true;

        string json = crafted.ToJson();
        Assert.True(ItemProperties.TryParse(json, out var again, out string error), error);

        Assert.Equal(json, again.ToJson());
        Assert.Equal((3, 812, 9L, 7L, true), (again.Enchant, again.Quality, again.CrafterId, again.OriginalOwner, again.Tainted));
        Assert.Equal(new[] { "m3.gemT1" }, again.Gems);
        Assert.Equal(new[] { ("atk", 14, 0), ("def", 0, -5) }, again.CraftedStats.Select(s => (s.StatId, s.Flat, s.Percent)));
        Assert.Equal(new[] { 3L }, again.OwnerHistory);
    }

    [Fact]
    public void Unknown_members_are_kept_sorted_after_the_known_fields()
    {
        const string stored = "{\"zeta\":{\"b\":[1,true,null],\"a\":\"x\\\"y\"},\"v\":2,\"alpha\":-1.5,\"enchant\":1}";

        Assert.True(ItemProperties.TryParse(stored, out var properties, out string error), error);

        Assert.Equal(2, properties.UnknownCount);
        string json = properties.ToJson();
        Assert.EndsWith(",\"tainted\":false,\"alpha\":-1.5,\"zeta\":{\"b\":[1,true,null],\"a\":\"x\\\"y\"}}", json);
        Assert.True(ItemProperties.TryParse(json, out var again, out _));
        Assert.Equal(json, again.ToJson());
        Assert.Equal(json, properties.Clone().ToJson());
    }

    [Fact]
    public void The_text_does_not_depend_on_the_stored_member_order()
    {
        Assert.True(ItemProperties.TryParse("{\"originalOwner\":5,\"enchant\":2,\"v\":2,\"b\":1,\"a\":2}", out var one, out _));
        Assert.True(ItemProperties.TryParse("{\"v\":2,\"a\":2,\"enchant\":2,\"b\":1,\"originalOwner\":5}", out var two, out _));

        Assert.Equal(one.ToJson(), two.ToJson());
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"v\":3}")]
    [InlineData("{\"v\":0}")]
    [InlineData("{\"v\":\"1\"}")]
    [InlineData("{\"v\":2,\"enchant\":-1}")]
    [InlineData("{\"v\":2,\"enchant\":256}")]
    [InlineData("{\"v\":2,\"quality\":65536}")]
    [InlineData("{\"v\":2,\"gems\":[1]}")]
    [InlineData("{\"v\":2,\"gems\":[\"\"]}")]
    [InlineData("{\"v\":2,\"craftedStats\":[{\"flat\":1}]}")]
    [InlineData("{\"v\":2,\"craftedStats\":[{\"stat\":\"atk\",\"flat\":1.5}]}")]
    [InlineData("{\"v\":2,\"crafterId\":-1}")]
    [InlineData("{\"v\":2,\"ownerHistory\":[\"a\"]}")]
    [InlineData("{\"v\":2,\"tainted\":1}")]
    public void Malformed_properties_and_later_versions_are_refused(string json)
    {
        Assert.False(ItemProperties.TryParse(json, out _, out string error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void Gems_and_crafted_stats_are_capped()
    {
        string gems = string.Join(",", Enumerable.Repeat("\"g\"", ItemProperties.MaxGems + 1));
        Assert.False(ItemProperties.TryParse($"{{\"v\":2,\"gems\":[{gems}]}}", out _, out _));
        string lines = string.Join(",", Enumerable.Repeat("{\"stat\":\"atk\",\"flat\":1}", ItemProperties.MaxCraftedStats + 1));
        Assert.False(ItemProperties.TryParse($"{{\"v\":2,\"craftedStats\":[{lines}]}}", out _, out _));

        string full = string.Join(",", Enumerable.Repeat("{\"stat\":\"atk\",\"flat\":1}", ItemProperties.MaxCraftedStats));
        Assert.True(ItemProperties.TryParse($"{{\"v\":2,\"craftedStats\":[{full}]}}", out var properties, out _));
        Assert.Equal(ItemProperties.MaxCraftedStats, properties.CraftedStats.Count);
    }

    [Fact]
    public void Strings_are_escaped()
    {
        var properties = ItemProperties.New(1);
        properties.Gems.Add("a\"b\\c\n\u0001");

        Assert.True(ItemProperties.TryParse(properties.ToJson(), out var again, out _));
        Assert.Equal("a\"b\\c\n\u0001", again.Gems[0]);
    }

    [Fact]
    public void A_clone_is_independent()
    {
        var original = ItemProperties.New(1);
        var copy = original.Clone();
        copy.CraftedStats.Add(new ItemStatBonus("atk", 1, 0));
        copy.Enchant = 2;

        Assert.False(original.HasCraftedStats);
        Assert.Equal(0, original.Enchant);
    }
}
