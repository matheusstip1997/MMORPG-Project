using GameServer.Entities;

namespace GameServer.Law;

/// <summary>
/// Whether one player may damage another (spec 6.1 <c>PvpRules.CanDamage</c>, R-6.1-01). The full rule: both players past
/// the first class change, the attacker armed (unless the region's <c>pvpMode</c> is always-on), not the same party or
/// guild, not blocked by the region, guild wars as valid targets.
/// <para>
/// M1: there is no class change yet (the first one comes at level 30, spec 12.3), so no player is past the first
/// one and players never damage players. TODO: the class change, the armed state, party and guild immunity with its
/// lockout, region <c>pvpMode</c> and wars (spec 6.1, 6.2).
/// </para>
/// </summary>
public static class PvpRules
{
    /// <param name="regionIndex">The region the hit happens in (<see cref="Entity.RegionIndex"/> of the attacker).</param>
    public static bool CanDamage(Player attacker, Player target, byte regionIndex) => false;
}
