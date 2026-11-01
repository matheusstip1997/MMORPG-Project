using Shared.Data.Tables;
using Shared.Simulation.Items;

namespace GameServer.Items;

/// <summary>
/// What a character's equipped gear gives (spec 4.1 M2 item model; the interface of WP-M2-1 to the stats of WP-M2-2 and the
/// weapon requirements of WP-M2-5). Implemented by <see cref="InventoryService"/> on the world thread. Reads the
/// <b>committed</b> equipped set (rule 5), the one the player was shown; <see cref="Events.EquipmentChanged"/> is published
/// when it changes. Keyed by the character id (<c>Player.CharacterId</c>); a character that is not attached has nothing.
/// The lists are cached per character and rebuilt on commit: no allocation per call, valid until the next change.
/// </summary>
public interface IEquipmentStatSource
{
    /// <summary>
    /// The stat bonuses of every equipped instance, in slot order: a crafted instance's stats snapshot as stored (its
    /// properties' <c>craftedStats</c>, spec 4.1 M3 item model), otherwise the <c>ItemStatBonusDef</c> rows of its item in
    /// file order, scaled by the instance's rarity (<c>RarityDef.statPercent</c>). Not summed: one entry per item and stat.
    /// The enchant bonus is apart (<see cref="GetEnchantBonuses"/>), and so are the socketed gems
    /// (<see cref="GetEquippedGemBonuses"/>, source <c>Gem</c>).
    /// </summary>
    IReadOnlyList<ItemStatBonus> GetEquippedBonuses(long characterId);

    /// <summary>
    /// The enchant lines of every equipped enchanted instance (spec 3.4 source <c>enchant</c>, 5.3 M3 decision 10; WP-M3-8):
    /// each of its stat lines above times <c>EnchantBonus.statPercent</c> of its level, read at stat time from the
    /// committed level, never baked into the item. Not summed; empty when nothing equipped is enchanted.
    /// </summary>
    IReadOnlyList<ItemStatBonus> GetEnchantBonuses(long characterId);

    /// <summary>
    /// The stats of the gems socketed in every equipped instance (R-5.3-05, spec 3.4 source <c>Gem</c>; WP-M3-9): each gem's
    /// <c>GemDef</c> row as it is, in slot order then socket order, read at stat time and never scaled by the item's rarity.
    /// Not summed: one entry per gem.
    /// </summary>
    IReadOnlyList<ItemStatBonus> GetEquippedGemBonuses(long characterId);

    /// <summary>The weapon types in the two weapon slots (R-3.5-07), main first; a dual weapon once.</summary>
    IReadOnlyList<WeaponType> GetEquippedWeaponTypes(long characterId);
}
