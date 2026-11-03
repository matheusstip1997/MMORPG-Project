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
    /// <summary>What the player does in the forge window; the binder turns it into requests (WP-M3-6, Forge v2 WP-M3-14).</summary>
    public interface IForgeWindowActions
    {
        /// <summary>A recipe row of the Recipes tab.</summary>
        void Choose(int row);

        /// <summary>The Next of a recipe input.</summary>
        void NextInput(int input);

        /// <summary>The Forge or Recipes tab (the Enchant tab is the enchant panel's).</summary>
        void ShowTab(ForgeTab tab);

        /// <summary>A click on the mold slot (<see cref="ForgeWindowViewModel.PickMold"/>) or a material slot: its picker.</summary>
        void Pick(int target);

        /// <summary>Entry <paramref name="index"/> of the open picker's list.</summary>
        void PickEntry(int index);

        void ClosePicker();

        /// <summary>Empties a material slot.</summary>
        void ClearInput(int input);

        /// <summary>A stack of container <paramref name="container"/> dropped on the mold slot (a drag from the inventory window).</summary>
        void DropOnMold(byte container, InventorySlotEntry entry);

        /// <summary>A stack dropped on material slot <paramref name="input"/>.</summary>
        void DropOnInput(int input, byte container, InventorySlotEntry entry);

        void StartForge();

        void UseAction(int index);

        void FinishForge();

        void CancelForge();

        void CloseForge();
    }

    /// <summary>
    /// Connects the forge window (spec 5.3, 9.13; WP-M3-6, Forge v2 WP-M3-14) to the world client: a station's yes
    /// (<see cref="NpcInteraction.Forge"/> or <see cref="NpcInteraction.JewelerBench"/>) opens it for that station (and the
    /// inventory window, to drag from); the Normal bag and the Safe bag's equipment area of
    /// <see cref="WorldClientFeed.InventoryReceived"/> feed the material and mold slots; drops and picker choices only select
    /// the stacks a start names (no inventory move); requests go out through
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

        /// <summary>
        /// The inventory window's open state and its setter (WP-M3-14): a station's yes opens it so the bags are there to drag
        /// from, and the forge closes it again only when it opened it. Null in a HUD without one.
        /// </summary>
        public Func<bool>? InventoryOpen { get; set; }

        public Action<bool>? SetInventoryOpen { get; set; }

        private bool _openedInventory;

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
            if (_openedInventory && InventoryOpen != null && !InventoryOpen())
                _openedInventory = false; // the player closed it: one they open again is theirs and stays
            FollowInventory();
        }

        public void Choose(int row)
        {
            if (!_window.InSession)
                _window.Select(row);
        }

        public void NextInput(int input) => _window.Cycle(input);

        public void ShowTab(ForgeTab tab) => _window.ShowTab(tab);

        public void Pick(int target) => _window.OpenPicker(target);

        /// <summary>A picker row: the mold or stack it showed when the picker opened (a list that changed since never shifts it).</summary>
        public void PickEntry(int index) => Toast(_window.PickRow(index, _config.NormalBag));

        public void ClosePicker() => _window.ClosePicker();

        public void ClearInput(int input) => _window.ClearInput(input);

        /// <summary>
        /// A stack dragged from the inventory window onto the mold slot (WP-M3-14): selects the mold, moves nothing; a refusal
        /// toasts.
        /// </summary>
        public void DropOnMold(byte container, InventorySlotEntry entry) => Toast(_window.DropMold(container, entry));

        /// <summary>A stack dragged onto material slot <paramref name="input"/>: selects the stack for the start, moves nothing.</summary>
        public void DropOnInput(int input, byte container, InventorySlotEntry entry) =>
            Toast(_window.Assign(input, container, entry, _config.NormalBag));

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
            FollowInventory();
        }

        private void OnInteracted(OutpostInteractResult result)
        {
            if ((result.Kind != NpcInteraction.Forge && result.Kind != NpcInteraction.JewelerBench) || result.Reason != OutpostReason.None)
                return;
            _window.OpenAt(result.NpcEntityId, result.Kind);
            if (InventoryOpen != null && SetInventoryOpen != null && !InventoryOpen())
            {
                SetInventoryOpen(true);
                _openedInventory = true;
            }
        }

        /// <summary>The forge closed: the inventory window it opened closes with it (one the player opened stays).</summary>
        private void FollowInventory()
        {
            if (!_openedInventory || _window.Open)
                return;
            _openedInventory = false;
            SetInventoryOpen?.Invoke(false);
        }

        private void Toast(string? refusal)
        {
            if (refusal != null)
                _toast.Show(refusal, false, _now(), _toastMs);
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

        /// <summary>The Normal bag feeds the material slots, the Safe bag's equipment area the mold slot (WP-M3-14).</summary>
        private void OnInventory(InventoryContents contents)
        {
            InventorySlotEntry[] molds = new InventorySlotEntry[0];
            foreach (var container in contents.Containers)
            {
                if (container.Code == _config.MoldContainer)
                    molds = container.Slots;
            }
            _window.SetBag(contents.Slots, molds, _config.MoldContainer);
        }

        private void OnDisconnected(string reasonCode)
        {
            _window.Clear();
            FollowInventory();
        }
    }
}
