namespace GameServer.Combat;

/// <summary>
/// The combat state of spec 12.7 (R-2.1-07): a player is in combat until <c>StartingValuesM1.combatStateMs</c> after its
/// last hit given or taken (<c>Player.LastCombatAtMs</c>, set by the HitResolver; 0 = never). Used by the disconnect linger
/// (R-2.1-08) and the gear change rule (spec 4.1 M2 item model).
/// </summary>
public static class CombatState
{
    public static bool IsInCombat(long lastCombatAtMs, long nowMs, int combatStateMs) =>
        lastCombatAtMs > 0 && nowMs < lastCombatAtMs + combatStateMs;
}
