#nullable enable
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.LowLevel;

namespace Game.Dev.Qa
{
    /// <summary>
    /// The QA run's test input source: a virtual keyboard and mouse ("QA Keyboard", "QA Mouse") added to the Input System.
    /// Their state goes in as ordinary input events, so the game's own actions (<c>GameInput</c>, the title's "any button")
    /// read them exactly as they read a player's devices. Which key an action uses is read from the live, enabled action
    /// bindings, never copied. While it runs, the other keyboards and mice are disabled and the input settings do not
    /// depend on window focus (batch mode has no focused Game view); <see cref="Dispose"/> restores both.
    /// <para>
    /// The settings are changed on the live settings object and put back on dispose, never swapped for another object:
    /// a project without an input settings asset runs on a temporary settings object that the Input System destroys when
    /// <c>InputSystem.settings</c> is replaced, and a replacement made in play mode is destroyed when play mode ends while
    /// the Input System still uses it (the "Assertion failed" in <c>InputManager.ApplySettings</c>, QA wave 3 F6).
    /// </para>
    /// </summary>
    public sealed class QaInput : IDisposable
    {
        public const string KeyboardName = "QA Keyboard";
        public const string MouseName = "QA Mouse";

        private readonly InputSettings _settings;
        private readonly InputSettings.BackgroundBehavior _backgroundBehavior;
        private readonly InputSettings.EditorInputBehaviorInPlayMode _editorInputBehavior;
        private readonly List<InputDevice> _disabled = new List<InputDevice>();
        private readonly HashSet<ButtonControl> _held = new HashSet<ButtonControl>();
        private readonly HashSet<ButtonControl> _touched = new HashSet<ButtonControl>();
        private readonly Dictionary<string, ButtonControl> _buttons = new Dictionary<string, ButtonControl>();
        private Vector2 _pointer;

        public QaInput()
        {
            _settings = InputSystem.settings;
            _backgroundBehavior = _settings.backgroundBehavior;
            _editorInputBehavior = _settings.editorInputBehaviorInPlayMode;
            _settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            _settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;

            foreach (var device in InputSystem.devices)
            {
                if ((device is Keyboard || device is Pointer) && device.enabled)
                {
                    InputSystem.DisableDevice(device);
                    _disabled.Add(device);
                }
            }
            Keyboard = InputSystem.AddDevice<Keyboard>(KeyboardName);
            Mouse = InputSystem.AddDevice<Mouse>(MouseName);
            Keyboard.MakeCurrent();
            Mouse.MakeCurrent();
        }

        public Keyboard Keyboard { get; }

        public Mouse Mouse { get; }

        /// <summary>
        /// The QA device button the enabled action <paramref name="actionName"/> is bound to (its first binding that resolves
        /// on the QA keyboard or mouse). Throws <see cref="QaFailure"/> when the action or a usable binding is missing.
        /// </summary>
        public ButtonControl ButtonFor(string actionName)
        {
            if (_buttons.TryGetValue(actionName, out var known))
                return known;
            foreach (var action in InputSystem.ListEnabledActions())
            {
                if (action.name != actionName)
                    continue;
                foreach (var binding in action.bindings)
                {
                    var path = binding.effectivePath;
                    if (string.IsNullOrEmpty(path))
                        continue;
                    var control = InputControlPath.TryFindControl(Keyboard, path) as ButtonControl
                                  ?? InputControlPath.TryFindControl(Mouse, path) as ButtonControl;
                    if (control != null)
                    {
                        _buttons[actionName] = control;
                        return control;
                    }
                }
                throw new QaFailure($"input action '{actionName}' has no keyboard or mouse button binding");
            }
            throw new QaFailure($"no enabled input action named '{actionName}' (did GameInput rename it?)");
        }

        /// <summary>Holds or releases a QA device button.</summary>
        public void Hold(ButtonControl button, bool down)
        {
            _touched.Add(button);
            if (down ? !_held.Add(button) : !_held.Remove(button))
                return;
            Send(button.device);
        }

        /// <summary>Moves the QA mouse to <paramref name="screen"/> (pixels, bottom-left origin like <c>Pointer.position</c>).</summary>
        public void Point(Vector2 screen)
        {
            _pointer = screen;
            Send(Mouse);
        }

        public void ReleaseAll()
        {
            foreach (var button in new List<ButtonControl>(_held))
                Hold(button, false);
        }

        public void Dispose()
        {
            ReleaseAll();
            if (Keyboard.added)
                InputSystem.RemoveDevice(Keyboard);
            if (Mouse.added)
                InputSystem.RemoveDevice(Mouse);
            foreach (var device in _disabled)
            {
                if (device.added)
                    InputSystem.EnableDevice(device);
            }
            _disabled.Clear();
            // The Input System also restores its edit-mode settings when play mode ends; put them back here as well, so
            // the run leaves nothing behind when it is disposed in edit mode or before play mode ends.
            if (_settings != null)
            {
                _settings.backgroundBehavior = _backgroundBehavior;
                _settings.editorInputBehaviorInPlayMode = _editorInputBehavior;
            }
        }

        /// <summary>
        /// Queues the whole QA state of <paramref name="device"/>. An event starts from the device's last processed state,
        /// so every button this run touched and the pointer are written each time: two events in one frame then agree.
        /// </summary>
        private void Send(InputDevice device)
        {
            using (StateEvent.From(device, out var eventPtr))
            {
                foreach (var button in _touched)
                {
                    if (button.device == device)
                        button.WriteValueIntoEvent(_held.Contains(button) ? 1f : 0f, eventPtr);
                }
                if (device == Mouse)
                    Mouse.position.WriteValueIntoEvent(_pointer, eventPtr);
                InputSystem.QueueEvent(eventPtr);
            }
        }
    }
}
