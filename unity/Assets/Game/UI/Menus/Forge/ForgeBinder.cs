#nullable enable
using System;
using Game.Gameplay.Entities;
using Game.UI.Config;
using Game.UI.ViewModels.Forge;
using Game.UI.ViewModels.Hud;
using Shared.Data.Tables;
using Shared.Protocol;

namespace Game.UI.Menus.Forge
{
    /// <summary>What the player does in the forge window; the binder turns it into requests (WP-M3-6).</summary>
    public interface IForgeWindowActions
    {
        void Choose(int row);

        void NextInput(int input);

        void StartForge();

        void UseAction(int index);

        void FinishForge();

        void CancelForge();

        void CloseForge();
    }

    /// <summary>
    /// Connects the forge window (spec 5.3, 9.13; WP-M3-6) to the world client: a station's yes (<see cref="NpcInteraction.Forge"/>
    /// or <see cref="NpcInteraction.JewelerBench"/>) opens it for that station; the Normal bag of
    /// <see cref="WorldClientFeed.InventoryReceived"/> feeds the input picker; requests go out through
    /// <see cref="WorldClientFeed.ForgeRequests"/>, one at a time; the session states and finish answers come back. Refusals show
    /// their <c>ReasonCodeStrings</c> text in the HUD toast, a made item a news toast. The window closes (a running session is
    /// cancelled by the server) once the station is no longer the NPC in reach.
    /// </summary>
    public sealed class ForgeBinder : IForgeWindowActions, IDisposable
    {
        private readonly ForgeConfig _config;
        private readonly UiText _text;
        private readonly ForgeWindowViewModel _window;
        private readonly ToastViewModel _toast;
        private readonly int _toastMs;
        private readonly Func<double> _now;
        private WorldClientFeed? _feed;

        public ForgeBinder(ForgeConfig config, UiText text, ForgeWindowViewModel window, ToastViewModel toast, int toastMs, Func<double> nowMs)
        {
            _config = config;
            _text = text;
            _window = window;
            _toast = toast;
            _toastMs = toastMs;
            _now = nowMs;
        }

        public WorldClientFeed? Feed => _feed;

        public void Bind(WorldClientFeed feed)
        {
            Unbind();
            _feed = feed;
            feed.ForgeStateReceived += OnState;
            feed.ForgeAnswered += OnAnswered;
            feed.InventoryReceived += OnInventory;
            feed.OutpostInteracted += OnInteracted;
            feed.Disconnected += OnDisconnected;
            if (feed.Inventory != null) // it may have come before the HUD bound
                OnInventory(feed.Inventory);
        }

        public void Unbind()
        {
            if (_feed == null)
                return;
            _feed.ForgeStateReceived -= OnState;
            _feed.ForgeAnswered -= OnAnswered;
            _feed.InventoryReceived -= OnInventory;
            _feed.OutpostInteracted -= OnInteracted;
            _feed.Disconnected -= OnDisconnected;
            _feed = null;
        }

        public void Dispose() => Unbind();

        /// <summary>Once per frame: the window closes once its station is no longer the NPC in reach.</summary>
        public void Frame(double nowMs)
        {
            if (_feed != null && _window.Open && _window.StationEntityId != _feed.NpcTarget)
                _window.Close();
        }

        public void Choose(int row)
        {
            if (!_window.InSession)
                _window.Select(row);
        }

        public void NextInput(int input) => _window.Cycle(input);

        public void StartForge()
        {
            if (!_window.CanStart || _window.Definition is not { } definition || _feed?.ForgeRequests == null)
                return;
            uint id = _feed.ForgeRequests.StartForge(_window.StationEntityId, definition.Kind, definition.NetId, _window.Inputs(_config.NormalBag));
            if (id != 0)
                _window.StartSent(id);
        }

        public void UseAction(int index)
        {
            if (!_window.CanAct || _window.Definition is not { } definition || index < 0 || index >= definition.Actions.Count
                || _feed?.ForgeRequests == null)
                return;
            uint id = _feed.ForgeRequests.ForgeAction(definition.Actions[index].NetId);
            if (id != 0)
                _window.ActionSent(id);
        }

        public void FinishForge()
        {
            if (!_window.InSession || _window.Finishing || _feed?.ForgeRequests == null)
                return;
            uint id = _feed.ForgeRequests.FinishForge();
            if (id != 0)
                _window.FinishSent(id);
        }

        public void CancelForge()
        {
            if (_window.InSession && !_window.Finishing)
                _feed?.ForgeRequests?.CancelForge();
        }

        public void CloseForge()
        {
            CancelForge(); // nothing is consumed (R-5.3-03)
            _window.Close();
        }

        private void OnInteracted(OutpostInteractResult result)
        {
            if ((result.Kind == NpcInteraction.Forge || result.Kind == NpcInteraction.JewelerBench) && result.Reason == OutpostReason.None)
                _window.OpenAt(result.NpcEntityId, result.Kind);
        }

        private void OnState(ForgeState state)
        {
            if (_window.SetState(state) is { } refused)
                _toast.Show(refused, false, _now(), _toastMs);
        }

        private void OnAnswered(ForgeResult result)
        {
            bool mine = result.RequestId == _window.PendingFinish && _window.PendingFinish != 0;
            if (_window.OnResult(result) is { } refused)
            {
                _toast.Show(refused, false, _now(), _toastMs);
                return;
            }
            if (!mine)
                return;
            string name = _config.Rules?.Item(result.OutputItemNetId) is { } item ? _text.Get(item.NameId) : result.OutputItemNetId.ToString();
            if (result.Success)
                _toast.ShowText(ForgeTextIds.ToastMade, "name", name, _now(), _toastMs);
            else
                _toast.ShowText(result.InputsKept ? ForgeTextIds.ToastInputsKept : ForgeTextIds.ToastConversionFailed, "name", name, _now(),
                    _toastMs);
        }

        private void OnInventory(InventoryContents contents) => _window.SetBag(contents.Slots);

        private void OnDisconnected(string reasonCode) => _window.Clear();
    }
}
