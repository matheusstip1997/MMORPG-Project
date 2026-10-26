#nullable enable
using Game.UI.Config;
using Game.UI.ViewModels;
using Shared.Data.Tables;
using UnityEngine.UIElements;

namespace Game.UI.Presenters
{
    /// <summary>
    /// The root of one login screen: elements are placed from <c>LoginLayout</c> (spec 9.10, same schema as the HUD,
    /// spec 9.7) in the reference resolution; the panel scales the whole screen (fit-height).
    /// </summary>
    public sealed class LoginScreenView
    {
        private readonly LoginConfig _config;
        private readonly string _screen;

        public LoginScreenView(LoginConfig config, string screen)
        {
            _config = config;
            _screen = screen;
            Root = new VisualElement { name = screen };
            Root.AddToClassList("login-screen");
        }

        public VisualElement Root { get; }

        /// <summary>Adds <paramref name="element"/> under its layout row. A missing row is a config error: logged, element hidden.</summary>
        public T Place<T>(string elementId, T element) where T : VisualElement
        {
            element.name = elementId;
            if (_config.TryGetLayout(_screen, elementId, out var row))
                UiLayout.Apply(element, row);
            else
            {
                UnityEngine.Debug.LogError($"[Login] LoginLayout has no row '{LoginLayout.IdFor(_screen, elementId)}'");
                element.style.display = DisplayStyle.None;
            }
            Root.Add(element);
            return element;
        }

        public void SetVisible(bool visible) => Root.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
    }

    /// <summary>
    /// Positions an element from a layout row: absolute, measured from its anchor (spec 9.7). The login screens
    /// (<c>LoginLayout</c>) and the HUD (<c>HudLayout</c>) share the schema and this code.
    /// </summary>
    public static class UiLayout
    {
        public static void Apply(VisualElement element, LoginLayout row) => Apply(element, row.Anchor, row.Offset, row.Size);

        public static void Apply(VisualElement element, HudLayout row) => Apply(element, row.Anchor, row.Offset, row.Size);

        /// <summary>
        /// The rectangle a row covers in a reference resolution of <paramref name="width"/> x <paramref name="height"/>
        /// (x, y from the top left), as <see cref="Apply(VisualElement, UiAnchor, int[], int[])"/> places it. For tests and
        /// for comparing with the absolute positions of spec 9.3.
        /// </summary>
        public static UnityEngine.RectInt Resolve(UiAnchor anchor, int[] offset, int[] size, int width, int height)
        {
            int x = Horizontal(anchor) switch
            {
                Edge.Start => offset[0],
                Edge.End => width - offset[0] - size[0],
                _ => width / 2 + offset[0] - size[0] / 2,
            };
            int y = Vertical(anchor) switch
            {
                Edge.Start => offset[1],
                Edge.End => height - offset[1] - size[1],
                _ => height / 2 + offset[1] - size[1] / 2,
            };
            return new UnityEngine.RectInt(x, y, size[0], size[1]);
        }

        public static void Apply(VisualElement element, UiAnchor anchor, int[] offset, int[] size)
        {
            var style = element.style;
            style.position = Position.Absolute;
            if (size[0] > 0)
                style.width = size[0];
            if (size[1] > 0)
                style.height = size[1];

            int x = offset[0];
            int y = offset[1];
            float translateX = 0f;
            float translateY = 0f;
            switch (Horizontal(anchor))
            {
                case Edge.Start:
                    style.left = x;
                    break;
                case Edge.End:
                    style.right = x;
                    break;
                default:
                    style.left = Length.Percent(50);
                    style.marginLeft = x;
                    translateX = -50f;
                    break;
            }
            switch (Vertical(anchor))
            {
                case Edge.Start:
                    style.top = y;
                    break;
                case Edge.End:
                    style.bottom = y;
                    break;
                default:
                    style.top = Length.Percent(50);
                    style.marginTop = y;
                    translateY = -50f;
                    break;
            }
            if (translateX != 0f || translateY != 0f)
                style.translate = new Translate(Length.Percent(translateX), Length.Percent(translateY));
        }

        private enum Edge
        {
            Start,
            Center,
            End,
        }

        private static Edge Horizontal(UiAnchor anchor)
        {
            switch (anchor)
            {
                case UiAnchor.TopLeft:
                case UiAnchor.MiddleLeft:
                case UiAnchor.BottomLeft:
                    return Edge.Start;
                case UiAnchor.TopRight:
                case UiAnchor.MiddleRight:
                case UiAnchor.BottomRight:
                    return Edge.End;
                default:
                    return Edge.Center;
            }
        }

        private static Edge Vertical(UiAnchor anchor)
        {
            switch (anchor)
            {
                case UiAnchor.TopLeft:
                case UiAnchor.TopCenter:
                case UiAnchor.TopRight:
                    return Edge.Start;
                case UiAnchor.BottomLeft:
                case UiAnchor.BottomCenter:
                case UiAnchor.BottomRight:
                    return Edge.End;
                default:
                    return Edge.Center;
            }
        }
    }

    /// <summary>Small element factories and the status line text shared by the presenters.</summary>
    public static class LoginWidgets
    {
        public static Label Label(string text, string className)
        {
            var label = new Label(text);
            label.AddToClassList("login-text");
            label.AddToClassList(className);
            return label;
        }

        public static Button Button(string text, System.Action onClick)
        {
            var button = new Button(onClick) { text = text };
            button.AddToClassList("login-button");
            return button;
        }

        public static VisualElement Panel(string className)
        {
            var panel = new VisualElement();
            panel.AddToClassList("login-panel");
            panel.AddToClassList(className);
            return panel;
        }

        /// <summary>The text of a status line: a busy text, a reason text from <c>ReasonCodeStrings</c>, or nothing.</summary>
        public static string StatusText(LoginStatus status, LoginConfig config, string clientProtocol)
        {
            switch (status.Kind)
            {
                case StatusKind.Busy:
                    return config.Text.Get(status.TextId);
                case StatusKind.Error:
                    return config.Reasons.Text(status.ReasonCode,
                        new TextArg("server", status.ServerProtocolVersion == 0 ? config.Text.Get(UiTextIds.ValueUnknown) : status.ServerProtocolVersion.ToString()),
                        new TextArg("client", clientProtocol));
                default:
                    return "";
            }
        }

        public static void ShowStatus(Label label, LoginStatus status, LoginConfig config, string clientProtocol)
        {
            label.text = StatusText(status, config, clientProtocol);
            label.EnableInClassList("login-message--error", status.Kind == StatusKind.Error);
        }
    }
}
