#nullable enable

namespace Game.UI.ViewModels.Hud
{
    /// <summary>
    /// The quick-item slot right of the hotbar (spec 3.4 consumable use, 9.13; WP-M3-3; <c>HudLayout</c> row <c>quick_item</c>):
    /// the bound consumable (0: none), how many the bag holds, the key that uses it and its cooldown as the client estimates
    /// it. The hotbar stays skills. Engine-free.
    /// </summary>
    public sealed class QuickItemViewModel : ViewModel
    {
        /// <summary>The bound consumable's item net id; 0 when nothing is bound.</summary>
        public ushort ItemNetId { get; private set; }

        /// <summary>Units of it in the Normal bag (0: bound but none left).</summary>
        public int Held { get; private set; }

        /// <summary>The key as the player reads it ("X").</summary>
        public string KeyName { get; private set; } = "";

        /// <summary>The display time its cooldown ends (0: ready), and how long it lasts, for the sweep.</summary>
        public double ReadyAtMs { get; private set; }

        public double CooldownMs { get; private set; }

        public bool IsBound => ItemNetId != 0;

        public void Set(ushort itemNetId, int held, string keyName)
        {
            if (itemNetId == ItemNetId && held == Held && keyName == KeyName)
                return;
            ItemNetId = itemNetId;
            Held = held;
            KeyName = keyName;
            NotifyChanged();
        }

        public void SetCooldown(double readyAtMs, double cooldownMs)
        {
            if (readyAtMs == ReadyAtMs && cooldownMs == CooldownMs)
                return;
            ReadyAtMs = readyAtMs;
            CooldownMs = cooldownMs;
            NotifyChanged();
        }

        /// <summary>The part of the cooldown left at <paramref name="nowMs"/>, 1 just used to 0 ready.</summary>
        public float CooldownLeft(double nowMs) =>
            CooldownMs <= 0 || nowMs >= ReadyAtMs ? 0f : (float)System.Math.Min(1.0, (ReadyAtMs - nowMs) / CooldownMs);
    }
}
