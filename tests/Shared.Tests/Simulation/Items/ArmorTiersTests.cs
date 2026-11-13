using Shared.Data.Tables;
using Shared.Simulation.Items;

namespace Shared.Tests.Simulation.Items;

/// <summary>The armor tier rule (R-4.1-05, WP-M4-13): one function for the server's equip check, the client and the bots.</summary>
public class ArmorTiersTests
{
    private static ItemDef Item(ArmorClass tier) => new() { Id = "x", Category = ItemCategory.Equipment, ArmorTier = tier };

    [Theory]
    [InlineData(ArmorClass.Light, ArmorClass.Light, true)]
    [InlineData(ArmorClass.Medium, ArmorClass.Heavy, false)]
    [InlineData(ArmorClass.Heavy, ArmorClass.Light, false)]
    [InlineData((ArmorClass)0, ArmorClass.Light, true)] // no tier: a weapon, a ring, an amulet
    public void A_character_wears_armor_of_its_own_tier_and_anything_that_is_not_armor(ArmorClass item, ArmorClass character, bool wears)
    {
        Assert.Equal(wears, ArmorTiers.CanWear(Item(item), character));
        Assert.Equal(item != 0, ArmorTiers.IsArmor(Item(item)));
    }
}
