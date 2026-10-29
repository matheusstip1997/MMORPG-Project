#nullable enable
using System;
using Shared.Data;
using Shared.Data.Tables;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.Gameplay.Dev
{
    /// <summary>
    /// A dev view's toggle key from its <see cref="Hotkeys"/> row (spec 9.7 key map; the dev rows of <see cref="DevTools"/>),
    /// as an Input System action, so no key is hardcoded and the QA run's virtual keyboard presses it like a real one.
    /// A missing row leaves the key unbound (logged): the validator requires the rows.
    /// </summary>
    public sealed class DevKey : IDisposable
    {
        private readonly InputAction? _action;

        private DevKey(InputAction? action)
        {
            _action = action;
        }

        public string Binding => _action?.bindings.Count > 0 ? _action.bindings[0].path : string.Empty;

        public static DevKey From(ConfigSet? config, string hotkeyId)
        {
            var row = config == null ? null : Hotkeys.Find(config, hotkeyId);
            if (row == null)
            {
                Debug.LogWarning($"[DevTools] no Hotkeys row '{hotkeyId}': its view has no key");
                return new DevKey(null);
            }
            var action = new InputAction(hotkeyId, InputActionType.Button, row.Binding);
            action.Enable();
            return new DevKey(action);
        }

        public bool WasPressedThisFrame() => _action != null && _action.WasPressedThisFrame();

        public void Dispose() => _action?.Dispose();
    }
}
