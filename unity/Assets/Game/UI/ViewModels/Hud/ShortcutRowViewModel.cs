#nullable enable
using System;
using Game.UI.Config;

namespace Game.UI.ViewModels.Hud
{
    /// <summary>
    /// What a shortcut button opens (WP-M2-11): the window's view model (its <c>Changed</c> redraws the button's open mark),
    /// whether the window is open, the toggle, and the key name the hover text shows.
    /// </summary>
    public sealed class ShortcutTarget
    {
        public ShortcutTarget(ViewModel window, Func<bool> isOpen, Action toggle, string keyName)
        {
            Window = window;
            IsOpen = isOpen;
            Toggle = toggle;
            KeyName = keyName;
        }

        public ViewModel Window { get; }

        public Func<bool> IsOpen { get; }

        public Action Toggle { get; }

        public string KeyName { get; }
    }

    /// <summary>
    /// Spec 9.3 <c>shortcut_row</c> (WP-M2-11): the menu buttons of the <c>HudLayout</c> row in their order, and the arrow
    /// that collapses the row to itself and expands it again. The collapsed state is a local client setting (spec 9.6),
    /// default expanded. The buttons open and close windows only; the row decides nothing about the game.
    /// </summary>
    public sealed class ShortcutRowViewModel : ViewModel
    {
        private readonly ILocalSettings _settings;

        public ShortcutRowViewModel(ILocalSettings settings, string[] buttons, bool collapsible)
        {
            _settings = settings;
            Buttons = buttons;
            Collapsible = collapsible;
            Collapsed = collapsible && settings.GetBool(LocalSettingKeys.ShortcutRowCollapsed, false);
        }

        /// <summary>The <c>HudLayout.buttons</c> ids, left to right after the arrow.</summary>
        public string[] Buttons { get; }

        public bool Collapsible { get; }

        public bool Collapsed { get; private set; }

        /// <summary>The arrow: hides every button, or shows them again; remembered on this machine.</summary>
        public void ToggleCollapsed()
        {
            if (!Collapsible)
                return;
            Collapsed = !Collapsed;
            _settings.SetBool(LocalSettingKeys.ShortcutRowCollapsed, Collapsed);
            NotifyChanged();
        }
    }
}
