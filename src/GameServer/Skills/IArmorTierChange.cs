using Shared.Data.Tables;
using Shared.Protocol;

namespace GameServer.Skills;

/// <summary>
/// What a skill tree operation that changes the character's armor tier does to the worn armor (spec 3.5 R-3.5-08, 4.1
/// R-4.1-05, M4 decision 9b; WP-M4-13): the character wears the new tier only, so the worn pieces of another tier come off
/// in the same operation (its one save). Implemented by <c>Items/ArmorTierSweep</c>, which writes only through
/// <c>InventoryService</c> and <c>MailService</c>; the skill tree only asks.
/// </summary>
public interface IArmorTierChange
{
    /// <summary>
    /// Before anything is applied: whether the worn armor can follow a change to <paramref name="tier"/>
    /// (<see cref="SkillTreeReason.None"/>), or why not (a worn piece a craft holds: <see cref="SkillTreeReason.ArmorReserved"/>).
    /// </summary>
    SkillTreeReason Check(long characterId, ArmorClass tier);

    /// <summary>
    /// The operation <paramref name="opKey"/> was applied and the tier is now <paramref name="tier"/>: take the worn pieces of
    /// another tier off (into the bag, by mail what does not fit) under keys derived from <paramref name="opKey"/>, on the save
    /// the operation asked for.
    /// </summary>
    void Apply(long characterId, ArmorClass tier, string opKey);
}
