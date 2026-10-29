using System.Collections.Generic;
using Game.Gameplay.Entities.Visuals;
using Game.Gameplay.Prediction;
using NUnit.Framework;
using Shared.Simulation.Actions;

namespace Game.Tests.EditMode.Entities
{
    /// <summary>The D2 -> visuals adapter: phase reports become presentation calls, remote ones at the render tick.</summary>
    public class ActionPresentationAdapterTests
    {
        private const int TickRateHz = 20;

        private sealed class RecordingView : IActionPresentation
        {
            public readonly List<string> Calls = new List<string>();

            public void OnActionPhase(string actionId, ActionPresentationPhase phase, uint phaseStartTick, int phaseDurationMs) =>
                Calls.Add($"phase {actionId} {phase} {phaseStartTick} {phaseDurationMs}");

            public void OnReaction(ReactionKind kind, uint startTick, int durationMs) => Calls.Add($"reaction {kind} {startTick} {durationMs}");

            public void OnActionEnd() => Calls.Add("end");
        }

        private static ActionPhaseChange Change(uint entity, ActionState phase, string actionId, uint phaseStart, int durationTicks) =>
            new ActionPhaseChange(entity, phase, actionId.Length > 0 ? (ushort)7 : (ushort)0, actionId, phaseStart, phaseStart,
                durationTicks, TickRateHz, isCorrection: false);

        [TestCase(ActionState.Startup, "phase skill.warrior.slash Startup 100 300")]
        [TestCase(ActionState.Channel, "phase skill.warrior.slash Channel 100 300")]
        [TestCase(ActionState.Active, "phase skill.warrior.slash Active 100 300")]
        [TestCase(ActionState.Recovery, "phase skill.warrior.slash Recovery 100 300")]
        public void Action_phases_map_to_their_presentation_phase_with_the_simulated_duration(ActionState phase, string expected)
        {
            var view = new RecordingView();

            ActionPresentationAdapter.Present(view, Change(1, phase, "skill.warrior.slash", 100, durationTicks: 6));

            Assert.That(view.Calls, Is.EqualTo(new[] { expected }));
        }

        [Test]
        public void Flinch_stagger_and_death_are_reactions_and_idle_ends_the_action()
        {
            var view = new RecordingView();

            ActionPresentationAdapter.Present(view, Change(1, ActionState.Flinched, "", 50, durationTicks: 4));
            ActionPresentationAdapter.Present(view, Change(1, ActionState.Staggered, "", 60, durationTicks: 30));
            ActionPresentationAdapter.Present(view, Change(1, ActionState.Dead, "", 70, durationTicks: 0));
            ActionPresentationAdapter.Present(view, Change(1, ActionState.Idle, "", 80, durationTicks: 0));

            Assert.That(view.Calls, Is.EqualTo(new[] { "reaction Flinched 50 200", "reaction Staggered 60 1500", "reaction Dead 70 0", "end" }));
        }

        [Test]
        public void A_stun_plays_the_stagger_reaction_for_its_length()
        {
            // WP-M2-9: Stunned (wire state 8) has no clip of its own yet; it plays the stagger reaction (placeholder).
            var view = new RecordingView();

            ActionPresentationAdapter.Present(view, Change(1, ActionState.Stunned, "", 40, durationTicks: 10));

            Assert.That(view.Calls, Is.EqualTo(new[] { "reaction Staggered 40 500" }));
        }

        [Test]
        public void Local_phases_are_presented_at_once()
        {
            var local = new RecordingView();
            var adapter = new ActionPresentationAdapter(() => local, _ => null);

            adapter.Local.OnActionPhase(Change(1, ActionState.Startup, "attack.warrior", 10, 4));

            Assert.That(local.Calls, Has.Count.EqualTo(1));
        }

        [Test]
        public void Local_phases_are_dropped_while_the_local_view_is_not_shown()
        {
            var adapter = new ActionPresentationAdapter(() => null, _ => null);

            Assert.DoesNotThrow(() => adapter.Local.OnActionPhase(Change(1, ActionState.Startup, "attack.warrior", 10, 4)));
        }

        [Test]
        public void Remote_phases_wait_for_the_render_tick_and_keep_their_order()
        {
            var remote = new RecordingView();
            var adapter = new ActionPresentationAdapter(() => null, id => id == 5 ? remote : null);

            adapter.Remote.OnActionPhase(Change(5, ActionState.Startup, "attack.warrior", 100, 4));
            adapter.Remote.OnActionPhase(Change(5, ActionState.Active, "attack.warrior", 104, 2));
            adapter.ReleaseRemote(99.5);
            Assert.That(remote.Calls, Is.Empty, "drawn behind the snapshot: not yet");

            adapter.ReleaseRemote(102.0);
            Assert.That(remote.Calls, Is.EqualTo(new[] { "phase attack.warrior Startup 100 200" }));
            Assert.That(adapter.PendingRemoteCount, Is.EqualTo(1));

            adapter.ReleaseRemote(104.0);
            Assert.That(remote.Calls, Is.EqualTo(new[] { "phase attack.warrior Startup 100 200", "phase attack.warrior Active 104 100" }));
            Assert.That(adapter.PendingRemoteCount, Is.Zero);
        }

        [Test]
        public void Forgotten_entities_lose_their_pending_phases_and_missing_views_are_skipped()
        {
            var remote = new RecordingView();
            var adapter = new ActionPresentationAdapter(() => null, id => id == 5 ? remote : null);
            adapter.Remote.OnActionPhase(Change(5, ActionState.Startup, "attack.warrior", 100, 4));
            adapter.Remote.OnActionPhase(Change(6, ActionState.Startup, "attack.warrior", 100, 4)); // no view
            adapter.Remote.OnActionPhase(Change(7, ActionState.Startup, "attack.warrior", 100, 4));

            adapter.Forget(7);
            adapter.ReleaseRemote(200);

            Assert.That(remote.Calls, Has.Count.EqualTo(1));
            Assert.That(adapter.PendingRemoteCount, Is.Zero);
        }
    }
}
