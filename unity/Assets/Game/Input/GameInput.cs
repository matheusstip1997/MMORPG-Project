#nullable enable
using System;
using Shared.Protocol;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.Gameplay.Input
{
    /// <summary>
    /// Input System actions for movement, aim, camera turn and actions (R-2.2-01, R-2.2-05). WASD moves (no click-to-move,
    /// R-2.2-04), the pointer sets the facing, C and V turn the camera (placeholder keys until the hotkey config of spec
    /// 9.9 exists). Left click attacks, Ctrl dodges, right click parries; the hotbar keys are 1 2 3 4 5 6 then Q E R F Z
    /// (slots 1 to 11). Bindings are made in code for now; they move to an input actions asset with rebinding later.
    /// TODO(later): interact (harvest, arrest).
    /// </summary>
    public sealed class GameInput : IDisposable
    {
        private readonly InputActionMap _map;
        private readonly InputAction _up;
        private readonly InputAction _left;
        private readonly InputAction _down;
        private readonly InputAction _right;
        private readonly InputAction _aim;
        private readonly InputAction _turnLeft;
        private readonly InputAction _turnRight;
        private readonly InputAction _attack;
        private readonly InputAction _dodge;
        private readonly InputAction _parry;
        private readonly InputAction[] _hotbar;

        /// <summary>Hotbar keys of R-2.2-01 in slot order: left side 1 2 3 / 4 5 6, right side Q E R / F Z.</summary>
        private static readonly string[] HotbarKeys =
        {
            "<Keyboard>/1", "<Keyboard>/2", "<Keyboard>/3", "<Keyboard>/4", "<Keyboard>/5", "<Keyboard>/6",
            "<Keyboard>/q", "<Keyboard>/e", "<Keyboard>/r", "<Keyboard>/f", "<Keyboard>/z",
        };

        public GameInput()
        {
            _map = new InputActionMap("Gameplay");
            _up = _map.AddAction("MoveUp", InputActionType.Button, "<Keyboard>/w");
            _left = _map.AddAction("MoveLeft", InputActionType.Button, "<Keyboard>/a");
            _down = _map.AddAction("MoveDown", InputActionType.Button, "<Keyboard>/s");
            _right = _map.AddAction("MoveRight", InputActionType.Button, "<Keyboard>/d");
            _aim = _map.AddAction("Aim", InputActionType.PassThrough, "<Pointer>/position", expectedControlLayout: "Vector2");
            _turnLeft = _map.AddAction("CameraTurnLeft", InputActionType.Button, "<Keyboard>/c");
            _turnRight = _map.AddAction("CameraTurnRight", InputActionType.Button, "<Keyboard>/v");
            _attack = _map.AddAction("Attack", InputActionType.Button, "<Mouse>/leftButton");
            _dodge = _map.AddAction("Dodge", InputActionType.Button, "<Keyboard>/leftCtrl");
            _dodge.AddBinding("<Keyboard>/rightCtrl");
            _parry = _map.AddAction("Parry", InputActionType.Button, "<Mouse>/rightButton");
            _hotbar = new InputAction[HotbarKeys.Length];
            for (int i = 0; i < HotbarKeys.Length; i++)
                _hotbar[i] = _map.AddAction("Hotbar" + (i + 1), InputActionType.Button, HotbarKeys[i]);
            _map.Enable();
        }

        /// <summary>Held movement keys, relative to the screen (see <see cref="ScreenRelativeKeys"/>).</summary>
        public MoveKeys ScreenMoveKeys
        {
            get
            {
                var keys = MoveKeys.None;
                if (_up.IsPressed()) keys |= MoveKeys.W;
                if (_left.IsPressed()) keys |= MoveKeys.A;
                if (_down.IsPressed()) keys |= MoveKeys.S;
                if (_right.IsPressed()) keys |= MoveKeys.D;
                return keys;
            }
        }

        /// <summary>Held action buttons (R-2.2-01); the simulation turns them into presses.</summary>
        public InputActions HeldActions
        {
            get
            {
                var actions = InputActions.None;
                if (_attack.IsPressed()) actions |= InputActions.Attack;
                if (_dodge.IsPressed()) actions |= InputActions.Dodge;
                if (_parry.IsPressed()) actions |= InputActions.Parry;
                return actions;
            }
        }

        /// <summary>The lowest hotbar slot whose key is held (1-based), 0 for none: an input frame names one slot.</summary>
        public byte HeldSkillSlot
        {
            get
            {
                for (int i = 0; i < _hotbar.Length; i++)
                {
                    if (_hotbar[i].IsPressed())
                        return (byte)(i + 1);
                }
                return 0;
            }
        }

        /// <summary>Pointer position in screen pixels.</summary>
        public Vector2 AimScreenPosition => _aim.ReadValue<Vector2>();

        /// <summary>-1 turns counter-clockwise (C), +1 clockwise (V), 0 when neither or both are held.</summary>
        public int CameraTurn => (_turnRight.IsPressed() ? 1 : 0) - (_turnLeft.IsPressed() ? 1 : 0);

        public void Dispose()
        {
            _map.Disable();
            _map.Dispose();
        }
    }

    /// <summary>Facing from the mouse: where the pointer ray meets the horizontal plane at the character's height.</summary>
    public static class GroundAim
    {
        public static bool TryFacing(Camera camera, Vector2 screenPosition, Vector3 characterWorld, out ushort facing)
        {
            facing = 0;
            var ray = camera.ScreenPointToRay(screenPosition);
            var plane = new Plane(Vector3.up, characterWorld);
            if (!plane.Raycast(ray, out float distance))
                return false;
            return Entities.WorldUnits.TryFacingFromDirection(ray.GetPoint(distance) - characterWorld, out facing);
        }
    }
}
