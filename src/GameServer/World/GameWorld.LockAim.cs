using GameServer.Combat;
using GameServer.Entities;
using Shared.Simulation;

namespace GameServer.World;

/// <summary>
/// The lock aim of M3 decision 16 (R-2.2-06, spec 3.1): under a target lock the server itself aims a player's presses that
/// are neither move nor ground skills at the locked enemy (<c>ActionMachine.AimOf</c>, <see cref="ActionAim.Target"/>).
/// </summary>
public sealed partial class GameWorld
{
    /// <summary>
    /// Phase 3, once per player per tick before its action step: when this tick's press frame is locked
    /// (<see cref="Player.CurrentLocked"/>), the angle from the player (after this tick's walk) to the
    /// <c>TargetLockChanged</c> target where this player saw it: the target's position history on the press frame's view clock
    /// (the clamped one phase 1 set, <see cref="DefenceTimeline.Lag64"/>; R-3.1-03), between two recorded ticks by the
    /// sub-tick. A target the server does not know (no message yet, gone), dead, the player itself, or not in this player's
    /// interest (out of view or hidden from it, <see cref="IVisibilityQuery"/>, rule 9) leaves the press at the mouse, as
    /// without a lock (<see cref="LockAimFallbacks"/>). The lock's range and break rules stay the client's (<c>TargetLock</c>).
    /// </summary>
    private void ResolveLockAim(Player player, uint tick)
    {
        player.HasLockAim = false;
        player.LockAim = 0;
        if (!player.HasInputThisTick || !player.CurrentLocked)
            return;
        var target = FindEntity(player.Inputs.LockTargetId);
        if (target == null || target == player || target.Combatant == null)
        {
            LockAimNoTarget++;
            return;
        }
        if (!target.Combatant.IsAlive)
        {
            LockAimDeadTarget++;
            return;
        }
        if (!player.Interest.Contains(target))
        {
            LockAimNotInView++;
            return;
        }
        long view64 = (long)tick * LockAim.SubTicks - player.Defence.Lag64;
        if (!target.Combatant.History.TryGetAtView64(view64, out var seen)
            || !LockAim.Toward(player.Movement.Position, seen, out ushort aim))
        {
            LockAimNoTarget++;
            return;
        }
        player.HasLockAim = true;
        player.LockAim = aim;
        LockAimsResolved++;
    }

    /// <summary>Locked press frames the server aimed at the locked enemy (M3 decision 16).</summary>
    public long LockAimsResolved { get; private set; }

    /// <summary>Locked press frames left at the mouse: no known target (no message yet, gone, not a combatant, no position).</summary>
    public long LockAimNoTarget { get; private set; }

    /// <summary>Locked press frames left at the mouse: the locked target is dead.</summary>
    public long LockAimDeadTarget { get; private set; }

    /// <summary>Locked press frames left at the mouse: the locked target is not in this player's interest (out of view or hidden).</summary>
    public long LockAimNotInView { get; private set; }

    /// <summary>Every locked press frame left at the mouse.</summary>
    public long LockAimFallbacks => LockAimNoTarget + LockAimDeadTarget + LockAimNotInView;
}
