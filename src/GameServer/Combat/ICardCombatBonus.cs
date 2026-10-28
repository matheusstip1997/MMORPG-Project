using GameServer.Entities;

namespace GameServer.Combat;

/// <summary>
/// Extra damage a hit gets from its attacker's Bystial book against the target's monster family (spec 14.2 R-14.2-05,
/// WP-M2-8): whole percents added to step 7 (<c>DamageInput.ExtraPercent</c>). Implemented by <c>BystialService</c>; 0 when
/// the attacker is no player or the target no monster. Called per hit in phase 4 and 5, allocation-free.
/// </summary>
public interface ICardCombatBonus
{
    int DamagePercent(Entity attacker, Entity target);
}
