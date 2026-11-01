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
    /// <summary>What the player does in the gems panel; the binder turns it into requests (WP-M3-9).</summary>
    public interface IGemPanelActions
    {
        void SelectGear(int row);

        void NextGem();

        void Socket();

        void Remove(int gemIndex);
    }

    /// <summary>
    /// Connects the gems panel (spec 5.3 R-5.3-05, 9.13; WP-M3-9) to the world client: it opens with the forge window at a
    /// jeweler's bench (<see cref="NpcInteraction.JewelerBench"/>'s yes) and closes with it; the committed contents of
    /// <see cref="WorldClientFeed.InventoryReceived"/> list the gear and the cut gems; the own jobs give the Jeweler level;
    /// requests go out through <see cref="WorldClientFeed.GemRequests"/>, one at a time; <see cref="WorldClientFeed.GemAnswered"/>
    /// brings refusals (their <c>ReasonCodeStrings</c> text in the HUD toast) and, once committed, the outcome as a news toast.
    /// </summary>
    public sealed class GemBinder : IGemPanelActions, IDisposable
    {
        private readonly GemConfig _config;
        private readonly UiText _text;
        private readonly GemPanelViewModel _panel;
        private readonly ForgeWindowViewModel _forge;
        private readonly ToastViewModel _toast;
        private readonly int _toastMs;
        private readonly Func<double> _now;
        private WorldClientFeed? _feed;

        public GemBinder(GemConfig config, UiText text, GemPanelViewModel panel, ForgeWindowViewModel forge, ToastViewModel toast,
            int toastMs, Func<double> nowMs)
        {
            _config = config;
            _text = text;
            _panel = panel;
            _forge = forge;
            _toast = toast;
            _toastMs = toastMs;
            _now = nowMs;
        }

        public void Bind(WorldClientFeed feed)
        {
            Unbind();
            _feed = feed;
            feed.GemAnswered += OnAnswered;
            feed.InventoryReceived += OnInventory;
            feed.OutpostInteracted += OnInteracted;
            feed.JobsReceived += OnJobs;
            feed.Disconnected += OnDisconnected;
            if (feed.Inventory != null) // it may have come before the HUD bound
                OnInventory(feed.Inventory);
            if (feed.JobsState != null)
                OnJobs(feed.JobsState);
        }

        public void Unbind()
        {
            if (_feed == null)
                return;
            _feed.GemAnswered -= OnAnswered;
            _feed.InventoryReceived -= OnInventory;
            _feed.OutpostInteracted -= OnInteracted;
            _feed.JobsReceived -= OnJobs;
            _feed.Disconnected -= OnDisconnected;
            _feed = null;
        }

        public void Dispose() => Unbind();

        /// <summary>Once per frame: the panel closes with the forge window (its station left reach or it was closed).</summary>
        public void Frame(double nowMs)
        {
            if (_panel.Open && (!_forge.Open || _forge.StationEntityId != _panel.StationEntityId))
                _panel.Close();
        }

        public void SelectGear(int row) => _panel.Select(row);

        public void NextGem() => _panel.NextGem();

        public void Socket()
        {
            if (!_panel.CanSocket || _panel.Target is not { } target || _panel.Gem is not { } gem || _feed?.GemRequests == null)
                return;
            uint id = _feed.GemRequests.SocketGem(_panel.StationEntityId, target, gem.Slot);
            if (id != 0)
                _panel.Sent(id);
        }

        public void Remove(int gemIndex)
        {
            if (!_panel.CanRemove(gemIndex) || _panel.Target is not { } target || _feed?.GemRequests == null)
                return;
            uint id = _feed.GemRequests.RemoveGem(_panel.StationEntityId, target, (byte)gemIndex);
            if (id != 0)
                _panel.Sent(id);
        }

        private void OnInteracted(OutpostInteractResult result)
        {
            if (result.Kind == NpcInteraction.JewelerBench && result.Reason == OutpostReason.None)
                _panel.OpenAt(result.NpcEntityId);
        }

        private void OnAnswered(GemResult result)
        {
            bool mine = result.RequestId == _panel.Pending && _panel.Pending != 0;
            if (_panel.OnResult(result) is { } refused)
            {
                _toast.Show(refused, false, _now(), _toastMs);
                return;
            }
            if (!mine)
                return;
            string gem = Name(result.GemItemNetId);
            string item = _panel.Target is { } target ? _text.Get(target.Item.NameId) : string.Empty;
            string id = result.Op == GemOp.Remove ? GemTextIds.ToastRemoved
                : result.Success ? GemTextIds.ToastSocketed : GemTextIds.ToastSocketFailed;
            _toast.ShowText(id, "gem", gem, "item", item, _now(), _toastMs);
        }

        private void OnInventory(InventoryContents contents) => _panel.SetContents(contents);

        /// <summary>The level of the job the gems are a service of (the Jeweler, from the tier-1 odds row).</summary>
        private void OnJobs(JobState state)
        {
            string jobId = _config.Rules?.Odds(1)?.JobId ?? string.Empty;
            int level = 0;
            if (_config.Jobs?.Find(jobId) is { } job)
            {
                foreach (var entry in state.Jobs)
                {
                    if (entry.JobNetId == job.NetId)
                        level = entry.Level;
                }
            }
            _panel.SetJobLevel(level);
        }

        private void OnDisconnected(string reasonCode) => _panel.Clear();

        private string Name(int netId) => _config.Rules?.Item(netId) is { } item ? _text.Get(item.NameId) : netId.ToString();
    }
}
