#nullable enable
using System;
using System.Collections.Generic;
using Game.Gameplay.Entities;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Actions;

namespace Game.Gameplay.Input
{
    /// <summary>A monster the target lock may pick, at its drawn position on the ground plane (simulation X and Y, cm).</summary>
    public struct LockCandidate
    {
        public uint Id;
        public float XCm;
        public float YCm;

        public LockCandidate(uint id, float xCm, float yCm)
        {
            Id = id;
            XCm = xCm;
            YCm = yCm;
        }
    }

    /// <summary>
    /// The target lock (R-2.2-06, user decision 2026-10-26), a facing aid: while it holds a target, the facing the client
    /// sends points at that target instead of the mouse. Attacks, shapes and effects still aim with the facing (spec 3.1
    /// Targeting), and R-2.2-02 still holds the facing during an action (the action machine ignores the frame facing then).
    /// <list type="bullet">
    /// <item>A press locks the nearest candidate within <c>acquireRangeCm</c>; with a lock on it cycles
    /// (<see cref="TargetCycleOrder"/>). A second press within <c>doublePressWindowMs</c> of the one before clears the lock
    /// instead (the first press already acted, so locking needs no wait).</item>
    /// <item><see cref="Refresh"/> clears the lock when the target is no candidate any more (dead, gone, out of interest)
    /// or farther than <c>breakRangeCm</c>. No line of sight in M1.</item>
    /// </list>
    /// Engine-free; allocates only when the visited set grows past its capacity.
    /// </summary>
    public sealed class TargetLock
    {
        private readonly HashSet<uint> _visited = new HashSet<uint>();
        private TargetLockSettings _settings;
        private double _lastPressMs;
        private bool _hasLastPress;
        private float _targetXCm;
        private float _targetYCm;

        public TargetLock(TargetLockSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        /// <summary>The locked entity, 0 when none.</summary>
        public uint LockedId { get; private set; }

        public bool IsLocked => LockedId != 0;

        /// <summary>Dev hot reload: new ranges and window from the reloaded table; the lock stays.</summary>
        public void Retune(TargetLockSettings settings) => _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        /// <summary>
        /// The lock key went down at <paramref name="nowMs"/> (any monotonic clock in ms) with the player at
        /// (<paramref name="selfXCm"/>, <paramref name="selfYCm"/>). Returns true when <see cref="LockedId"/> changed.
        /// </summary>
        public bool Press(double nowMs, float selfXCm, float selfYCm, List<LockCandidate> candidates)
        {
            bool doublePress = _hasLastPress && nowMs - _lastPressMs <= _settings.DoublePressWindowMs;
            if (doublePress && IsLocked)
            {
                _hasLastPress = false; // a third press starts over: it locks again
                return Clear();
            }
            _lastPressMs = nowMs;
            _hasLastPress = true;

            uint previous = LockedId;
            int next = IsLocked ? Cycle(selfXCm, selfYCm, candidates) : Nearest(selfXCm, selfYCm, candidates, skipVisited: false);
            if (!IsLocked)
                _visited.Clear(); // a new lock starts a new round
            if (next < 0)
                return false; // nothing (else) in range: keep what there is
            Lock(candidates[next]);
            return LockedId != previous;
        }

        /// <summary>
        /// Every client tick: follows the target to its drawn position, or clears the lock when it is no candidate any more
        /// or beyond <c>breakRangeCm</c>. Returns true when the lock was cleared.
        /// </summary>
        public bool Refresh(float selfXCm, float selfYCm, List<LockCandidate> candidates)
        {
            if (!IsLocked)
                return false;
            for (int i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                if (candidate.Id != LockedId)
                    continue;
                if (DistanceSq(selfXCm, selfYCm, candidate) > Square(_settings.BreakRangeCm))
                    return Clear();
                _targetXCm = candidate.XCm;
                _targetYCm = candidate.YCm;
                return false;
            }
            return Clear();
        }

        /// <summary>Drops the lock (disconnect, own death, double press). Returns true when one was on.</summary>
        public bool Clear()
        {
            if (!IsLocked)
                return false;
            LockedId = 0;
            _visited.Clear();
            return true;
        }

        /// <summary>
        /// The facing toward the locked target's last refreshed position (binary angle, clockwise from +Y). False when
        /// unlocked or standing on the target's point, where the caller keeps its facing.
        /// </summary>
        public bool TryFacing(float selfXCm, float selfYCm, out ushort facing)
        {
            facing = 0;
            if (!IsLocked)
                return false;
            return TryFacingToward(_targetXCm - selfXCm, _targetYCm - selfYCm, out facing);
        }

        /// <summary>The binary-angle facing (65536 per turn, clockwise from +Y) toward (dx, dy); false for a zero vector.</summary>
        public static bool TryFacingToward(double dxCm, double dyCm, out ushort facing)
        {
            facing = 0;
            if (dxCm * dxCm + dyCm * dyCm < 1e-6)
                return false;
            double turns = Math.Atan2(dxCm, dyCm) / (2 * Math.PI);
            if (turns < 0)
                turns += 1;
            facing = (ushort)((int)Math.Round(turns * 65_536) & 0xFFFF);
            return true;
        }

        private void Lock(in LockCandidate candidate)
        {
            LockedId = candidate.Id;
            _targetXCm = candidate.XCm;
            _targetYCm = candidate.YCm;
            _visited.Add(candidate.Id);
        }

        private int Cycle(float selfXCm, float selfYCm, List<LockCandidate> candidates)
        {
            if (_settings.CycleOrder == TargetCycleOrder.Clockwise)
                return NextClockwise(selfXCm, selfYCm, candidates);
            int next = Nearest(selfXCm, selfYCm, candidates, skipVisited: true);
            if (next >= 0)
                return next;
            // Every candidate in range was locked in this round: a new round from the nearest other than the current one.
            _visited.Clear();
            _visited.Add(LockedId);
            return Nearest(selfXCm, selfYCm, candidates, skipVisited: true);
        }

        private int Nearest(float selfXCm, float selfYCm, List<LockCandidate> candidates, bool skipVisited)
        {
            int best = -1;
            double bestSq = Square(_settings.AcquireRangeCm);
            for (int i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                if (candidate.Id == LockedId || skipVisited && _visited.Contains(candidate.Id))
                    continue;
                double sq = DistanceSq(selfXCm, selfYCm, candidate);
                if (sq > bestSq || sq == bestSq && best >= 0 && candidate.Id > candidates[best].Id)
                    continue;
                best = i;
                bestSq = sq;
            }
            return best;
        }

        private int NextClockwise(float selfXCm, float selfYCm, List<LockCandidate> candidates)
        {
            double from = Angle(_targetXCm - selfXCm, _targetYCm - selfYCm);
            double rangeSq = Square(_settings.AcquireRangeCm);
            int best = -1;
            double bestTurn = double.MaxValue;
            double bestSq = double.MaxValue;
            for (int i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                double sq = DistanceSq(selfXCm, selfYCm, candidate);
                if (candidate.Id == LockedId || sq > rangeSq)
                    continue;
                double turn = Angle(candidate.XCm - selfXCm, candidate.YCm - selfYCm) - from;
                if (turn <= 0)
                    turn += 2 * Math.PI; // the same direction as the current target comes last
                if (turn > bestTurn || turn == bestTurn && sq >= bestSq)
                    continue;
                best = i;
                bestTurn = turn;
                bestSq = sq;
            }
            return best;
        }

        /// <summary>Clockwise from +Y, 0 to 2 pi.</summary>
        private static double Angle(double dx, double dy)
        {
            double angle = Math.Atan2(dx, dy);
            return angle < 0 ? angle + 2 * Math.PI : angle;
        }

        private static double DistanceSq(float selfXCm, float selfYCm, in LockCandidate candidate)
        {
            double dx = candidate.XCm - selfXCm;
            double dy = candidate.YCm - selfYCm;
            return dx * dx + dy * dy;
        }

        private static double Square(int cm) => (double)cm * cm;
    }

    /// <summary>The entities the target lock may pick (R-2.2-06; M1: living monsters and bosses, no players).</summary>
    public static class TargetCandidates
    {
        /// <summary>A monster that is alive (not in the dead state, HP above 0) and not leaving interest.</summary>
        public static bool IsLockable(EntityKind kind, ActionState state, byte hpBucket, bool leaving) =>
            kind == EntityKind.Monster && state != ActionState.Dead && hpBucket > 0 && !leaving;

        /// <summary>Fills <paramref name="buffer"/> with the lockable entities at their drawn position at <paramref name="renderTick"/>.</summary>
        public static void Collect(EntityRegistry registry, double renderTick, List<LockCandidate> buffer)
        {
            buffer.Clear();
            foreach (var entity in registry.Entities.Values)
            {
                if (!IsLockable(entity.Kind, entity.ActionState, entity.HpBucket, entity.LeaveTick.HasValue))
                    continue;
                if (!entity.Samples.TrySample(renderTick, out var pose))
                    continue;
                buffer.Add(new LockCandidate(entity.Id, pose.XCm, pose.ZCm)); // snapshot Z is the ground-plane Y
            }
        }
    }
}
