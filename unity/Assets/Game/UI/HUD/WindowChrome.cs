#nullable enable
using System;
using Game.UI.Config;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.UI.HUD
{
    /// <summary>
    /// The parts every window shares (spec 9.13 window pattern; WP-M4-11): the header (the title in the accent color, a close
    /// button with its text label for screen readers, spec 9.8), the body's scroll view, the text field look (typed text in the
    /// <c>text</c> token on a dark field, user 2026-11-17, QA M4 wave 1 D3) and the stolen mark of an item row (R-4.4-01). Built in
    /// code like the rest of the HUD; colors and sizes from the theme tokens. Placeholder art.
    /// </summary>
    public static class WindowChrome
    {
        public const string HeaderClass = "window-header";
        public const string TitleClass = "window-title";
        public const string CloseClass = "window-close";
        public const string FieldClass = "window-field";
        public const string StolenMarkClass = "window-stolen";

        /// <summary>
        /// A header: the title and, when <paramref name="close"/> is given, a close button named <paramref name="closeName"/> with the
        /// <c>UiStrings</c> text <paramref name="closeTextId"/> and the hover label <paramref name="closeLabelId"/>. Extra elements
        /// (a count, a split bar) go between them through <paramref name="middle"/>.
        /// </summary>
        public static VisualElement Header(HudConfig config, string title, string closeName, string closeTextId, string closeLabelId,
            Action? close, out Label titleLabel, out Button? closeButton, VisualElement? middle = null)
        {
            var theme = config.Theme;
            var header = HudWidgets.Box(HeaderClass);
            header.style.flexDirection = FlexDirection.Row;
            header.style.justifyContent = Justify.SpaceBetween;
            header.style.alignItems = Align.Center;
            header.pickingMode = PickingMode.Position;
            titleLabel = HudWidgets.Label(title, TitleClass);
            titleLabel.style.color = theme.Accent;
            titleLabel.style.flexShrink = 1;
            titleLabel.style.whiteSpace = WhiteSpace.Normal;
            header.Add(titleLabel);
            if (middle != null)
                header.Add(middle);
            closeButton = null;
            if (close != null)
            {
                closeButton = Button(theme, closeName, config.Text.Get(closeTextId), close);
                closeButton.AddToClassList(CloseClass);
                closeButton.tooltip = config.Text.Get(closeLabelId);
                header.Add(closeButton);
            }
            return header;
        }

        /// <summary>A plain HUD button in the theme (not focusable: the keys stay the game's).</summary>
        public static Button Button(HudTheme theme, string name, string text, Action onClick)
        {
            var button = new Button(onClick) { focusable = false, name = name, text = text };
            button.style.color = theme.Text;
            button.style.backgroundColor = theme.Panel;
            HudWidgets.Border(button, theme.Border);
            return button;
        }

        /// <summary>A vertical scroll view that takes the height left in its window (a longer list scrolls; spec 9.13).</summary>
        public static ScrollView Scroll(string name)
        {
            var scroll = new ScrollView(ScrollViewMode.Vertical) { name = name };
            scroll.style.flexGrow = 1;
            scroll.style.flexShrink = 1;
            scroll.pickingMode = PickingMode.Position;
            return scroll;
        }

        /// <summary>
        /// The one look of a window text field (user 2026-11-17, D3): typed text in the <c>text</c> token on the panel's dark color
        /// with the border token, so a typed name never reads like a grey hint on white.
        /// </summary>
        public static TextField Field(TextField field, HudTheme theme)
        {
            field.AddToClassList(FieldClass);
            var input = field.Q(className: TextField.inputUssClassName) ?? field;
            input.style.color = theme.Text;
            input.style.backgroundColor = theme.Panel;
            HudWidgets.Border(input, theme.Border);
            return field;
        }

        /// <summary>
        /// The stolen mark of an item (R-4.4-01, spec 9.13; WP-M4-11): a small diamond in the debuff color with the short tag
        /// (<c>inv.slot.stolen</c>) on it, the hover label "Stolen". Shape and text, not color alone (9.8). Only a flag and a mark group
        /// reach the client, never the owner. Absolute in its row's or slot's top left corner; hidden until <see cref="SetStolen"/>.
        /// </summary>
        public static VisualElement StolenMark(HudConfig config)
        {
            var theme = config.Theme;
            int size = theme.Tokens.FontSizeSlotLabel + 3;
            var mark = HudWidgets.Box(StolenMarkClass);
            mark.style.position = Position.Absolute;
            mark.style.left = 1;
            mark.style.top = 1;
            mark.style.width = size;
            mark.style.height = size;
            mark.tooltip = config.Text.Get(InventoryTextIds.TooltipStolen);
            var diamond = HudWidgets.Box(StolenMarkClass + "__shape");
            diamond.style.position = Position.Absolute;
            diamond.style.left = 2;
            diamond.style.top = 2;
            diamond.style.width = size - 4;
            diamond.style.height = size - 4;
            diamond.style.backgroundColor = theme.DebuffBorder;
            diamond.style.rotate = new Rotate(new Angle(45f, AngleUnit.Degree));
            mark.Add(diamond);
            var tag = HudWidgets.Label(config.Text.Get(InventoryTextIds.SlotStolen), StolenMarkClass + "__tag");
            tag.style.position = Position.Absolute;
            tag.style.left = 0;
            tag.style.top = 0;
            tag.style.width = size;
            tag.style.height = size;
            tag.style.fontSize = theme.Tokens.FontSizeSlotLabel;
            tag.style.color = theme.Text;
            tag.style.unityTextAlign = TextAnchor.MiddleCenter;
            mark.Add(tag);
            mark.style.display = DisplayStyle.None;
            return mark;
        }

        /// <summary>Shows or hides a <see cref="StolenMark"/>.</summary>
        public static void SetStolen(VisualElement mark, bool stolen) =>
            mark.style.display = stolen ? DisplayStyle.Flex : DisplayStyle.None;
    }
}
