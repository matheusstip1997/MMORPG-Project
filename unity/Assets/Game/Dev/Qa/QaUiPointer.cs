#nullable enable
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Dev.Qa
{
    /// <summary>
    /// The QA run's mouse as UI Toolkit sees it (QA wave 6 F1). UI Toolkit's runtime event system drops pointer input while
    /// the application has no focus (<c>DefaultEventSystem</c>, "IgnoreIfAppNotFocused"), and a batch run never has focus,
    /// so the QA mouse of <see cref="QaInput"/> never reached a panel. This sends the same pointer events the event system
    /// would (built from IMGUI mouse events, at panel positions) to the panel's root: UI Toolkit's own dispatch then picks
    /// the element under the pointer, sends the enter and leave events, honours pointer capture and runs the elements'
    /// handlers, exactly as for a player's mouse. Only the hop from the input device to the panel is skipped.
    /// </summary>
    public sealed class QaUiPointer
    {
        private bool _pressed;

        /// <summary>Where the pointer is, in panel coordinates.</summary>
        public Vector2 Position { get; private set; } = new Vector2(-1f, -1f);

        public bool Pressed => _pressed;

        /// <summary>Shift held with the next events (WP-M2-11: Shift+click sets up a stack split).</summary>
        public bool Shift { get; set; }

        /// <summary>Moves the pointer to <paramref name="at"/> (a drag while the left button is down).</summary>
        public void Move(IPanel panel, Vector2 at)
        {
            Position = at;
            Send(panel, _pressed ? EventType.MouseDrag : EventType.MouseMove);
        }

        /// <summary>Presses the left button where the pointer is.</summary>
        public void Down(IPanel panel)
        {
            _pressed = true;
            Send(panel, EventType.MouseDown);
        }

        /// <summary>Releases the left button where the pointer is.</summary>
        public void Up(IPanel panel)
        {
            _pressed = false;
            Send(panel, EventType.MouseUp);
        }

        /// <summary>
        /// A right-click where the pointer is (WP-M3-3: it uses a consumable in the inventory window). Only the panel sees it:
        /// the QA mouse's right button (parry) is not pressed.
        /// </summary>
        public void RightClick(IPanel panel)
        {
            Send(panel, EventType.MouseDown, button: 1);
            Send(panel, EventType.MouseUp, button: 1);
        }

        /// <summary>The middle of an element, in panel coordinates.</summary>
        public static Vector2 Center(VisualElement element) => element.worldBound.center;

        private void Send(IPanel panel, EventType type, int button = 0)
        {
            var imgui = new Event
            {
                type = type,
                mousePosition = Position,
                button = button,
                clickCount = type == EventType.MouseDown ? 1 : 0,
                modifiers = Shift ? EventModifiers.Shift : EventModifiers.None,
            };
            EventBase e = type switch
            {
                EventType.MouseDown => PointerDownEvent.GetPooled(imgui),
                EventType.MouseUp => PointerUpEvent.GetPooled(imgui),
                _ => PointerMoveEvent.GetPooled(imgui),
            };
            using (e)
                panel.visualTree.SendEvent(e);
        }
    }
}
