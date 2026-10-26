#nullable enable
using System.Collections.Generic;
using Game.Gameplay.Input;
using NUnit.Framework;
using Shared.Data.Tables;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace Game.Dev.Qa.EditorTests
{
    /// <summary>
    /// The smoke run's <c>lock-demon</c> step (R-2.2-06) through <see cref="QaInput"/> and <see cref="GameInput"/>: each
    /// press holds the key <see cref="QaTimeouts.PressSeconds"/>, then one frame released (QaScenario.Press), and one
    /// <see cref="InputSystem.Update()"/> is one frame at <see cref="QaTimeouts.FrameRate"/>. When the facing settles at once
    /// the step's double press used to start about 220 ms after the lock press, so its first tap paired with that press,
    /// cleared, and the second tap locked again (QA: the archer smoke, 2026-10-26).
    /// </summary>
    public sealed class QaTargetLockInputTests : InputTestFixture
    {
        private const string TabPath = "<Keyboard>/tab";
        private const uint Demon = 1;

        private static readonly TargetLockSettings Settings = new TargetLockSettings
        {
            DoublePressWindowMs = 300, AcquireRangeCm = 1500, BreakRangeCm = 2000, CycleOrder = TargetCycleOrder.Distance,
        };

        private readonly List<LockCandidate> _candidates = new List<LockCandidate> { new LockCandidate(Demon, 0, 1092) };
        private QaInput? _qa;
        private GameInput? _game;
        private ButtonControl? _key;
        private TargetLock? _lock;
        private double _nowMs;

        public override void Setup()
        {
            base.Setup();
            _qa = new QaInput();
            _game = new GameInput(TabPath);
            _key = _qa.ButtonForPath(TabPath);
            _lock = new TargetLock(Settings);
            _nowMs = 0;
        }

        public override void TearDown()
        {
            _game?.Dispose();
            _qa?.Dispose();
            base.TearDown();
        }

        [Test]
        public void The_steps_double_press_clears_the_lock_even_when_it_follows_the_lock_press_at_once()
        {
            double lockPressedAt = _nowMs;
            Press();
            Assert.That(_lock!.LockedId, Is.EqualTo(Demon), "one press locks the demon");

            // The step's wait before its double press, the facing and the screenshot taking no time at all.
            double waitMs = QaTimeouts.SecondsBeforeDoublePress(Settings.DoublePressWindowMs, (float)((_nowMs - lockPressedAt) / 1000.0)) * 1000.0;
            Frames(waitMs);
            Press();
            Press();

            Assert.That(_lock.IsLocked, Is.False, "the double press clears the lock");
        }

        [Test]
        public void A_double_press_right_after_the_lock_press_pairs_its_first_tap_with_it()
        {
            Press();
            Press(); // within the window of the lock press: the second press of a double press, it clears
            Assert.That(_lock!.IsLocked, Is.False);
            Press(); // a third press starts over and locks again: what the step saw
            Assert.That(_lock.LockedId, Is.EqualTo(Demon));
        }

        [Test]
        public void The_wait_ends_a_margin_past_the_window_and_is_never_negative()
        {
            Assert.That(QaTimeouts.SecondsBeforeDoublePress(300, 0f), Is.EqualTo(0.3f + QaTimeouts.LockDoublePressMarginSeconds).Within(1e-5f));
            Assert.That(QaTimeouts.SecondsBeforeDoublePress(300, 0.4f), Is.EqualTo(0.1f).Within(1e-5f));
            Assert.That(QaTimeouts.SecondsBeforeDoublePress(300, 2f), Is.EqualTo(0f));
        }

        /// <summary>QaScenario.Press: down, held for PressSeconds of frames, up, one more frame.</summary>
        private void Press()
        {
            _qa!.Hold(_key!, true);
            Frames(QaTimeouts.PressSeconds * 1000.0);
            _qa.Hold(_key!, false);
            Frame();
        }

        /// <summary>Frames until <paramref name="ms"/> have passed, at least one (QaScenario.Wait checks after each frame).</summary>
        private void Frames(double ms)
        {
            double until = _nowMs + ms;
            do
                Frame();
            while (_nowMs < until);
        }

        /// <summary>One frame: the input update, then what PredictionDevClient.Update does with the key.</summary>
        private void Frame()
        {
            _nowMs += 1000.0 / QaTimeouts.FrameRate;
            InputSystem.Update();
            if (_game!.TargetLockPressed)
                _lock!.Press(_nowMs, 0, 0, _candidates);
        }
    }
}
