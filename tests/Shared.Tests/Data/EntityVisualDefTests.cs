using Shared.Data;
using Shared.Data.Tables;

namespace Shared.Tests.Data;

public class EntityVisualDefTests
{
    private static EntityVisualDef Row() => new()
    {
        Id = "assist",
        PrefabAddress = "Visuals/Characters/Assist",
        WeaponAddress = "Visuals/Weapons/Wand",
        WeaponBone = VisualBone.RightHand,
        OffHandAddress = "Visuals/Weapons/RoundShield",
        OffHandBone = VisualBone.LeftHand,
        ScalePercent = 100,
    };

    private static IReadOnlyList<(string? Field, string Message)> Errors(EntityVisualDef row)
    {
        var errors = new RowErrors();
        row.Validate(errors);
        return errors.Items;
    }

    [Fact]
    public void A_complete_row_is_valid()
    {
        Assert.Empty(Errors(Row()));
    }

    [Fact]
    public void A_row_without_attachments_is_valid()
    {
        var row = Row();
        row.WeaponAddress = "";
        row.WeaponBone = VisualBone.None;
        row.OffHandAddress = "";
        row.OffHandBone = VisualBone.None;

        Assert.Empty(Errors(row));
    }

    [Fact]
    public void The_prefab_address_is_required()
    {
        var row = Row();
        row.PrefabAddress = "";

        Assert.Equal(nameof(EntityVisualDef.PrefabAddress), Assert.Single(Errors(row)).Field);
    }

    [Theory]
    [InlineData("Visuals//Warrior")]
    [InlineData("/Visuals/Warrior")]
    [InlineData("Visuals/Warrior/")]
    [InlineData("Visuals/War rior")]
    [InlineData("Visuals\\Warrior")]
    public void Malformed_addresses_are_refused(string address)
    {
        var row = Row();
        row.PrefabAddress = address;

        Assert.Equal(nameof(EntityVisualDef.PrefabAddress), Assert.Single(Errors(row)).Field);
    }

    [Fact]
    public void A_weapon_needs_a_bone()
    {
        var row = Row();
        row.WeaponBone = VisualBone.None;

        Assert.Equal(nameof(EntityVisualDef.WeaponBone), Assert.Single(Errors(row)).Field);
    }

    [Fact]
    public void A_bone_without_an_off_hand_is_refused()
    {
        var row = Row();
        row.OffHandAddress = "";

        Assert.Equal(nameof(EntityVisualDef.OffHandBone), Assert.Single(Errors(row)).Field);
    }

    [Fact]
    public void Weapon_and_off_hand_cannot_share_a_bone()
    {
        var row = Row();
        row.OffHandBone = VisualBone.RightHand;

        Assert.Equal(nameof(EntityVisualDef.OffHandBone), Assert.Single(Errors(row)).Field);
    }

    [Fact]
    public void Scale_is_range_checked()
    {
        var row = Row();
        row.ScalePercent = 0;
        var set = new ConfigSet();
        set.Add(new ConfigTable(typeof(EntityVisualDef), 1, new object[] { row }));

        var error = Assert.Single(TableValidator.Validate(set));

        Assert.Equal(ValidationCodes.OutOfRange, error.Code);
        Assert.Equal(nameof(EntityVisualDef.ScalePercent), error.Field);
    }
}
