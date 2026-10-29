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
    /// The stat bonuses of every equipped instance (<c>ItemStatBonusDef</c> rows of its item, in slot order and file order),
    /// already scaled by the instance's rarity (<c>RarityDef.statPercent</c>). Not summed: one entry per item and stat.
    /// </summary>
    IReadOnlyList<ItemStatBonus> GetEquippedBonuses(long characterId);

    /// <summary>The weapon types in the two weapon slots (R-3.5-07), main first; a dual weapon once.</summary>
    IReadOnlyList<WeaponType> GetEquippedWeaponTypes(long characterId);
}
