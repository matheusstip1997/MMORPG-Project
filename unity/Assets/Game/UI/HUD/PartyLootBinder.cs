#nullable enable
using System;
using Game.Gameplay.Entities;
using Game.UI.Config;
using Game.UI.ViewModels.Hud;
using Shared.Protocol;

namespace Game.UI.HUD
{
    /// <summary>
    /// Party loot rolls on the HUD (spec 4.3 R-4.3-02 "M4 party loot", 9.12; WP-M4-6). Each <see cref="LootRollNotice"/> is one
    /// winner's part of a roll, which the server sends to every member once the winner's save committed it. It becomes a line on
    /// the chat's Party tab ("Bo won 2 Demon Horn.", with the units that went by mail when the bag was full), and a toast when
    /// the winner is the own player. The names come from the server through <c>DisplayIdentity</c>, the texts from
    /// <c>UiStrings</c>; the client decides nothing.
    /// </summary>
    public sealed class PartyLootBinder : IDisposable
    {
        private readonly ChatViewModel _chat;
        private readonly ToastViewModel _toast;
        private readonly InventoryConfig _inventory;
        private readonly UiText _text;
        private readonly int _toastMs;
        private readonly Func<double> _now;
        private WorldClientFeed? _feed;

        public PartyLootBinder(ChatViewModel chat, ToastViewModel toast, InventoryConfig inventory, UiText text, int toastMs, Func<double> nowMs)
        {
            _chat = chat;
            _toast = toast;
            _inventory = inventory;
            _text = text;
            _toastMs = toastMs;
            _now = nowMs;
        }

        public void Bind(WorldClientFeed feed)
        {
            Unbind();
            _feed = feed;
            feed.PartyLootRolled += OnRolled;
        }

        public void Unbind()
        {
            if (_feed == null)
                return;
            _feed.PartyLootRolled -= OnRolled;
            _feed = null;
        }

        public void Dispose() => Unbind();

        private void OnRolled(LootRollNotice notice)
        {
            var item = _inventory.Item(notice.ItemNetId);
            string itemName = item != null ? _text.Get(item.Def.NameId) : "";
            int total = notice.Units + notice.Mailed;
            string count = total.ToString();
            string line = notice.Mailed > 0
                ? _text.Get(HudTextIds.PartyLootRollMail, new TextArg("name", notice.WinnerName), new TextArg("count", count),
                    new TextArg("item", itemName), new TextArg("mailed", notice.Mailed.ToString()))
                : _text.Get(HudTextIds.PartyLootRoll, new TextArg("name", notice.WinnerName), new TextArg("count", count),
                    new TextArg("item", itemName));
            _chat.AddLootLine(line);
            uint own = _feed?.Registry.OwnEntityId ?? 0;
            if (own == 0 || notice.WinnerEntityId != own)
                return;
            // The toast takes two arguments: with units by mail it names how many wait there (the party line has the whole split).
            if (notice.Mailed > 0)
                _toast.ShowText(HudTextIds.ToastLootWonMail, "item", itemName, "mailed", notice.Mailed.ToString(), _now(), _toastMs);
            else
                _toast.ShowText(HudTextIds.ToastLootWon, "count", count, "item", itemName, _now(), _toastMs);
        }
    }
}
