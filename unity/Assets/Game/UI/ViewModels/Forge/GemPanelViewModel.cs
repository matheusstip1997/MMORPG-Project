#nullable enable
using System.Collections.Generic;
using Game.Gameplay.Entities;
using Shared.Protocol;

namespace Game.UI.ViewModels.Forge
{
    /// <summary>
    /// The gems panel's state (spec 5.3 R-5.3-05, 9.13; WP-M3-9): open while the forge window is open at a jeweler's bench; the
    /// gear with gem slots (worn first) and the cut gems of the committed bag, as <see cref="ClientGems"/> lists them; the
    /// chosen gear and gem; the request waiting for its answer. It only describes; the server checks every request and rolls
    /// every socket.
    /// </summary>
    public sealed class GemPanelViewModel : ViewModel
    {
        private readonly ClientGems? _gems;
        private readonly List<GemTarget> _targets = new List<GemTarget>();
        private readonly List<InventorySlotEntry> _bagGems = new List<InventorySlotEntry>();
        private InventoryContents? _contents;
        private int _gemIndex;

        public GemPanelViewModel(ClientGems? gems)
        {
            _gems = gems;
        }

        public bool Open { get; private set; }

        public uint StationEntityId { get; private set; }

        public IReadOnlyList<GemTarget> Targets => _targets;

        /// <summary>The chosen gear (index into <see cref="Targets"/>), -1 for none.</summary>
        public int Selected { get; private set; } = -1;

        public GemTarget? Target => Selected >= 0 && Selected < _targets.Count ? _targets[Selected] : (GemTarget?)null;

        /// <summary>The cut gem a socket would use, or null when the bag holds none.</summary>
        public InventorySlotEntry? Gem => _bagGems.Count == 0 ? null : _bagGems[_gemIndex % _bagGems.Count];

        public int GemCount => _bagGems.Count;

        /// <summary>The Jeweler level of the gem's job (0: not held), set by the binder from the own jobs.</summary>
        public int JobLevel { get; private set; }

        public uint Pending { get; private set; }

        public InventoryContents? Contents => _contents;

        public ClientGems? Gems => _gems;

        public bool CanSocket => Pending == 0 && Target is { } target && _gems != null
                                 && _gems.CheckSocket(target, Gem, JobLevel) == GemReason.None;

        public bool CanRemove(int index) => Pending == 0 && Target is { } target && _gems != null
                                           && _gems.CheckRemove(target, index, JobLevel, _contents) == GemReason.None;

        public void OpenAt(uint stationEntityId)
        {
            Open = true;
            StationEntityId = stationEntityId;
            Rebuild();
            NotifyChanged();
        }

        public void Close()
        {
            if (!Open)
                return;
            Open = false;
            Pending = 0;
            NotifyChanged();
        }

        public void Clear()
        {
            Open = false;
            Pending = 0;
            _contents = null;
            Selected = -1;
            Rebuild();
            NotifyChanged();
        }

        public void SetContents(InventoryContents contents)
        {
            _contents = contents;
            Rebuild();
            NotifyChanged();
        }

        public void SetJobLevel(int level)
        {
            if (JobLevel == level)
                return;
            JobLevel = level;
            NotifyChanged();
        }

        public void Select(int index)
        {
            if (index < 0 || index >= _targets.Count || Pending != 0)
                return;
            Selected = index;
            NotifyChanged();
        }

        public void NextGem()
        {
            if (_bagGems.Count < 2 || Pending != 0)
                return;
            _gemIndex = (_gemIndex + 1) % _bagGems.Count;
            NotifyChanged();
        }

        public void Sent(uint requestId)
        {
            Pending = requestId;
            NotifyChanged();
        }

        /// <summary>An answer: the refusal's reason code to show, or null (an applied operation, or not this panel's).</summary>
        public string? OnResult(GemResult result)
        {
            if (result.RequestId != Pending || Pending == 0)
                return null;
            Pending = 0;
            NotifyChanged();
            return result.Reason == GemReason.None ? null : result.Reason.ToString();
        }

        private void Rebuild()
        {
            long selected = Target is { } target ? target.Entry.InstanceId : 0;
            _gems?.Targets(_contents, _targets);
            _gems?.Gems(_contents, _bagGems);
            Selected = _targets.FindIndex(t => t.Entry.InstanceId == selected);
            if (Selected < 0 && _targets.Count > 0)
                Selected = 0;
            if (_gemIndex >= _bagGems.Count)
                _gemIndex = 0;
        }
    }
}
