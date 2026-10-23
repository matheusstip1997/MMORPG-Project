#nullable enable
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Dev.Qa
{
    /// <summary>
    /// The QA run's UI driver for the login screens (a test input source, clearly not a player's mouse): it finds elements
    /// by the layout ids the presenters give them (<c>LoginScreenView.Place</c> names each element after its
    /// <c>LoginLayout</c> row), and "clicks" a button by sending it a <see cref="NavigationSubmitEvent"/>, which is what
    /// Enter on a focused button sends. The button's own handler then calls the login flow, as for a player. Disabled
    /// buttons are never clicked: the scenario waits until the screen enables them.
    /// </summary>
    public sealed class QaLoginDriver
    {
        /// <summary>The class <c>LoginWidgets.ShowStatus</c> puts on a message that shows an error.</summary>
        public const string ErrorClass = "login-message--error";

        private VisualElement? _root;

        /// <summary>Finds the login scene's UI document; false while it is not loaded.</summary>
        public bool TryAttach()
        {
            _root = null;
            foreach (var document in Object.FindObjectsByType<UIDocument>(FindObjectsSortMode.None))
            {
                if (document.rootVisualElement != null && document.rootVisualElement.ClassListContains("login-root"))
                {
                    _root = document.rootVisualElement;
                    return true;
                }
            }
            return false;
        }

        /// <summary>True when the screen root named <paramref name="screen"/> (a <c>LoginScreens</c> id) is shown.</summary>
        public bool IsShown(string screen)
        {
            var element = _root?.Q(screen);
            return element != null && element.resolvedStyle.display != DisplayStyle.None
                   && element.style.display != DisplayStyle.None;
        }

        public T? Find<T>(string screen, string elementId) where T : VisualElement => _root?.Q(screen)?.Q<T>(elementId);

        public string TextOf(string screen, string elementId) => Find<Label>(screen, elementId)?.text ?? "";

        /// <summary>The error a screen's message label shows, or "" when it shows none.</summary>
        public string ErrorOn(string screen, string messageId)
        {
            var label = Find<Label>(screen, messageId);
            return label != null && label.ClassListContains(ErrorClass) ? label.text : "";
        }

        /// <summary>Buttons under <paramref name="containerId"/> of a screen, in order (server rows, slot cards, classes).</summary>
        public List<Button> ButtonsIn(string screen, string containerId)
        {
            var buttons = new List<Button>();
            _root?.Q(screen)?.Q(containerId)?.Query<Button>().ForEach(buttons.Add);
            return buttons;
        }

        public static bool CanClick(Button? button) => button != null && button.enabledInHierarchy;

        /// <summary>Sends a submit event to an enabled button; false when it is missing or disabled.</summary>
        public static bool Click(Button? button)
        {
            if (!CanClick(button))
                return false;
            using (var submit = NavigationSubmitEvent.GetPooled())
            {
                submit.target = button;
                button!.SendEvent(submit);
            }
            return true;
        }

        /// <summary>Types into a text field: setting the value raises the same change event as typing.</summary>
        public static bool Type(TextField? field, string text)
        {
            if (field == null || !field.enabledInHierarchy)
                return false;
            field.value = text;
            return true;
        }
    }
}
