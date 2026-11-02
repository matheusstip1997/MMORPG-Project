#nullable enable
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Dev.Qa
{
    /// <summary>
    /// Key events sent through UI Toolkit to the focused element of a field, the way the engine delivers a player's key
    /// press to a runtime panel (a key code event, then the character). The QA keyboard is an Input System device: the
    /// game's actions and the HUD's polling read it, but a UI Toolkit panel takes its keys from the engine's own event
    /// path, which no Input System device reaches. So the run presses Enter on the QA keyboard for the HUD and sends the
    /// same press here to the field, and types a line as characters.
    /// </summary>
    public static class QaUiKeys
    {
        /// <summary>The focused element when it is <paramref name="field"/> or inside it (a text field's inner text element), else null.</summary>
        public static VisualElement? FocusedIn(VisualElement field)
        {
            var focused = field.focusController?.focusedElement as VisualElement;
            return focused != null && (focused == field || field.Contains(focused)) ? focused : null;
        }

        /// <summary>A key press on the focused element inside <paramref name="field"/>; false when it has no focus.</summary>
        public static bool Press(VisualElement field, KeyCode key, char character)
        {
            var target = FocusedIn(field);
            if (target == null)
                return false;
            Send(target, key, '\0');
            if (character != '\0')
                Send(target, KeyCode.None, character);
            return true;
        }

        /// <summary>Types <paramref name="text"/> into the focused element inside <paramref name="field"/>; false when it has no focus.</summary>
        public static bool Type(VisualElement field, string text)
        {
            var target = FocusedIn(field);
            if (target == null)
                return false;
            foreach (char c in text)
                Send(target, KeyCode.None, c);
            return true;
        }

        private static void Send(VisualElement target, KeyCode key, char character)
        {
            using (var e = KeyDownEvent.GetPooled(character, key, EventModifiers.None))
            {
                e.target = target;
                target.SendEvent(e);
            }
        }
    }
}
