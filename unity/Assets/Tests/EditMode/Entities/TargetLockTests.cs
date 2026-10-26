using System.Collections.Generic;
using Game.Gameplay.Input;
using NUnit.Framework;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Actions;

namespace Game.Tests.EditMode.Entities
{
    /// <summary>
    /// The target lock (R-2.2-06, user decision 2026-10-26): acquiring, cycling (nearest not yet visited, or clockwise), the
    /// double press that clears it, what breaks it, the facing it gives, and which entities it may pick.
    /// </summary>
    public class TargetLockTests
    {
        private const int WindowMs = 300;

        private static TargetLock Lock(TargetCycleOrder order = TargetCycleOrder.Distance) => new TargetLock(new TargetLockSettings
        {
            DoublePressWindowMs = WindowMs, AcquireRangeCm = 1500, BreakRangeCm = 2000, CycleOrder = order,
        });

        private static List<LockCandidate> Candidates(params (uint id, float x, float y)[] entities)
        {
            var list = new List<LockCandidate>();
            foreach (var (id, x, y) in entities)
                list.Add(new LockCandidate(id, x, y));
            return list;
        }

        [Test]
        public void A_press_locks_the_nearest_candidate_in_range()
        {
            var targetLock = Lock();
            var candidates = Candidates((1, 0, 900), (2, 500, 0), (3, 0, -1400));

            Assert.IsTrue(targetLock.Press(0, 0, 0, candidates));

            Assert.AreEqual(2u, targetLock.LockedId);
        }

        [Test]
        public void Nothing_in_the_acquire_range_locks_nothing()
        {
            var targetLock = Lock();

            Assert.IsFalse(targetLock.Press(0, 0, 0, Candidates((1, 1600, 0), (2, 0, -1501))));

            Assert.IsFalse(targetLock.IsLocked);
            Assert.IsFalse(targetLock.TryFacing(0, 0, out _));
        }

        [Test]
        public void Presses_cycle_through_the_nearest_not_yet_visited_then_start_a_new_round()
        {
            var targetLock = Lock();
            var candidates = Candidates((1, 300, 0), (2, 0, 600), (3, -900, 0));
            var locked = new List<uint>();
            for (int i = 0; i < 5; i++)
            {
                targetLock.Press(i * 1000, 0, 0, candidates); // presses farther apart than the double-press window
                locked.Add(targetLock.LockedId);
            }

            CollectionAssert.AreEqual(new uint[] { 1, 2, 3, 1, 2 }, locked);
        }

        [Test]
        public void A_target_that_moves_closer_does_not_make_the_cycle_skip_or_repeat()
        {
            var targetLock = Lock();
            targetLock.Press(0, 0, 0, Candidates((1, 300, 0), (2, 0, 600), (3, -900, 0)));
            targetLock.Press(1000, 0, 0, Candidates((1, 300, 0), (2, 0, 600), (3, -900, 0)));
            Assert.AreEqual(2u, targetLock.LockedId);

            // 1 walks in front of the player; it was already visited in this round, so 3 comes next.
            targetLock.Press(2000, 0, 0, Candidates((1, 50, 0), (2, 0, 600), (3, -900, 0)));

            Assert.AreEqual(3u, targetLock.LockedId);
        }

        [Test]
        public void With_one_candidate_a_press_keeps_the_lock()
        {
            var targetLock = Lock();
            var candidates = Candidates((7, 300, 0));
            targetLock.Press(0, 0, 0, candidates);

            Assert.IsFalse(targetLock.Press(1000, 0, 0, candidates));

            Assert.AreEqual(7u, targetLock.LockedId);
        }

        [Test]
        public void Clockwise_order_moves_around_the_player_from_the_current_target()
        {
            var targetLock = Lock(TargetCycleOrder.Clockwise);
            // North (nearest), west, east, south: clockwise from north is east, south, west.
            var candidates = Candidates((1, 0, 200), (2, -800, 0), (3, 900, 0), (4, 0, -1000));
            var locked = new List<uint>();
            for (int i = 0; i < 5; i++)
            {
                targetLock.Press(i * 1000, 0, 0, candidates);
                locked.Add(targetLock.LockedId);
            }

            CollectionAssert.AreEqual(new uint[] { 1, 3, 4, 2, 1 }, locked);
        }

        [Test]
        public void A_second_press_within_the_window_clears_the_lock()
        {
            var targetLock = Lock();
            var candidates = Candidates((1, 300, 0), (2, 0, 600));
            targetLock.Press(0, 0, 0, candidates);

            Assert.IsTrue(targetLock.Press(WindowMs, 0, 0, candidates));

            Assert.IsFalse(targetLock.IsLocked);
            // A third press starts over: it locks the nearest again.
            targetLock.Press(WindowMs + 50, 0, 0, candidates);
            Assert.AreEqual(1u, targetLock.LockedId);
        }

        [Test]
        public void A_second_press_after_the_window_cycles()
        {
            var targetLock = Lock();
            var candidates = Candidates((1, 300, 0), (2, 0, 600));
            targetLock.Press(0, 0, 0, candidates);

            targetLock.Press(WindowMs + 1, 0, 0, candidates);

            Assert.AreEqual(2u, targetLock.LockedId);
        }

        [Test]
        public void A_double_press_after_a_cycle_ends_unlocked()
        {
            var targetLock = Lock();
            var candidates = Candidates((1, 300, 0), (2, 0, 600));
            targetLock.Press(0, 0, 0, candidates);
            targetLock.Press(1000, 0, 0, candidates); // cycles to 2 at once

            targetLock.Press(1100, 0, 0, candidates); // the double press

            Assert.IsFalse(targetLock.IsLocked);
        }

        [Test]
        public void The_lock_breaks_beyond_the_break_range_but_not_between_acquire_and_break()
        {
            var targetLock = Lock();
            targetLock.Press(0, 0, 0, Candidates((1, 1000, 0)));

            Assert.IsFalse(targetLock.Refresh(0, 0, Candidates((1, 1900, 0))));
            Assert.IsTrue(targetLock.IsLocked);
            Assert.IsTrue(targetLock.Refresh(0, 0, Candidates((1, 2001, 0))));
            Assert.IsFalse(targetLock.IsLocked);
        }

        [Test]
        public void The_lock_breaks_when_the_target_is_no_candidate_any_more()
        {
            var targetLock = Lock();
            targetLock.Press(0, 0, 0, Candidates((1, 300, 0), (2, 600, 0)));

            // Dead, despawned or out of interest: the collector no longer lists it.
            Assert.IsTrue(targetLock.Refresh(0, 0, Candidates((2, 600, 0))));

            Assert.IsFalse(targetLock.IsLocked);
        }

        [Test]
        public void Clear_drops_the_lock_once()
        {
            var targetLock = Lock();
            targetLock.Press(0, 0, 0, Candidates((1, 300, 0)));

            Assert.IsTrue(targetLock.Clear());
            Assert.IsFalse(targetLock.Clear());
        }

        [TestCase(0f, 100f, 0)]
        [TestCase(100f, 100f, 8192)]
        [TestCase(100f, 0f, 16384)]
        [TestCase(0f, -100f, 32768)]
        [TestCase(-100f, 0f, 49152)]
        [TestCase(-100f, 100f, 57344)]
        public void The_facing_points_at_the_target_clockwise_from_plus_y(float dx, float dy, int expected)
        {
            var targetLock = Lock();
            targetLock.Press(0, 500, 500, Candidates((1, 500 + dx, 500 + dy)));

            Assert.IsTrue(targetLock.TryFacing(500, 500, out ushort facing));

            Assert.AreEqual(expected, facing);
        }

        [Test]
        public void The_facing_follows_the_refreshed_target_position()
        {
            var targetLock = Lock();
            targetLock.Press(0, 0, 0, Candidates((1, 0, 300)));

            targetLock.Refresh(0, 0, Candidates((1, 300, 0)));

            Assert.IsTrue(targetLock.TryFacing(0, 0, out ushort facing));
            Assert.AreEqual(16384, facing);
        }

        [Test]
        public void Standing_on_the_target_point_gives_no_facing()
        {
            var targetLock = Lock();
            targetLock.Press(0, 0, 0, Candidates((1, 0, 0)));

            Assert.IsTrue(targetLock.IsLocked);
            Assert.IsFalse(targetLock.TryFacing(0, 0, out _));
        }

        [Test]
        public void Only_living_monsters_that_stay_in_interest_are_lockable()
        {
            Assert.IsTrue(TargetCandidates.IsLockable(EntityKind.Monster, ActionState.Idle, 63, false));
            Assert.IsTrue(TargetCandidates.IsLockable(EntityKind.Monster, ActionState.Active, 1, false));
            Assert.IsFalse(TargetCandidates.IsLockable(EntityKind.Player, ActionState.Idle, 63, false));
            Assert.IsFalse(TargetCandidates.IsLockable(EntityKind.LootDrop, ActionState.Idle, 63, false));
            Assert.IsFalse(TargetCandidates.IsLockable(EntityKind.None, ActionState.Idle, 63, false));
            Assert.IsFalse(TargetCandidates.IsLockable(EntityKind.Monster, ActionState.Dead, 63, false));
            Assert.IsFalse(TargetCandidates.IsLockable(EntityKind.Monster, ActionState.Idle, 0, false));
            Assert.IsFalse(TargetCandidates.IsLockable(EntityKind.Monster, ActionState.Idle, 63, true));
        }
    }
}
