#nullable enable
using System.Linq;
using NUnit.Framework;
using UnityEngine.InputSystem;

namespace Game.Dev.Qa.EditorTests
{
    /// <summary>
    /// <see cref="QaInput"/> setup and teardown. <see cref="InputTestFixture"/> runs the Input System on a temporary
    /// settings object, like this project (it has no input settings asset) in the smoke run.
    /// </summary>
    public sealed class QaInputTests : InputTestFixture
    {
        [Test]
        public void Keeps_the_input_settings_object_and_restores_its_values()
        {
            // QA wave 3 F6: swapping InputSystem.settings destroyed the temporary settings object, and the swapped-in copy
            // was destroyed when play mode ended while still in use ("Assertion failed" in InputManager.ApplySettings).
            var settings = InputSystem.settings;
            var background = settings.backgroundBehavior;
            var editor = settings.editorInputBehaviorInPlayMode;

            using (new QaInput())
            {
                Assert.That(InputSystem.settings, Is.SameAs(settings));
                Assert.That(settings != null, Is.True, "the settings object is still alive");
                Assert.That(settings!.backgroundBehavior, Is.EqualTo(InputSettings.BackgroundBehavior.IgnoreFocus));
                Assert.That(settings.editorInputBehaviorInPlayMode,
                    Is.EqualTo(InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView));
            }

            Assert.That(InputSystem.settings, Is.SameAs(settings));
            Assert.That(settings != null, Is.True, "the settings object is still alive");
            Assert.That(settings!.backgroundBehavior, Is.EqualTo(background));
            Assert.That(settings.editorInputBehaviorInPlayMode, Is.EqualTo(editor));
        }

        [Test]
        public void Removes_its_devices_and_enables_the_others_again()
        {
            var keyboard = InputSystem.AddDevice<Keyboard>();

            using (new QaInput())
                Assert.That(keyboard.enabled, Is.False, "other keyboards are disabled during the run");

            Assert.That(keyboard.enabled, Is.True);
            Assert.That(InputSystem.devices.Select(d => d.name), Has.None.EqualTo(QaInput.KeyboardName).And.None.EqualTo(QaInput.MouseName));
        }
    }
}
