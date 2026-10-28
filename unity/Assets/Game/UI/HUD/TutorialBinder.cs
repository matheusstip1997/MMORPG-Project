#nullable enable
using System;
using System.Collections.Generic;
using Game.Gameplay.Entities;
using Game.UI.Config;
using Game.UI.Menus.Outpost;
using Game.UI.ViewModels.Hud;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using UnityEngine.InputSystem;

namespace Game.UI.HUD
{
    /// <summary>
    /// Connects the tutorial (spec 12.3, R-12.3-01 to 03; WP-M2-10) to the world client: the own level drives the
    /// <see cref="ClientTutorial"/> schedule and the HUD by level (<see cref="HudView.ApplyLevel"/>); the facts of each frame
    /// (a drop in pickup reach, the kind of the NPC in reach, a committed sale) are its triggers; the prediction's gate stop
    /// (<c>LocalPlayerPredictor.GateStopLevel</c>) shows the gate's refusal for <c>TutorialSettings.gatePromptHoldMs</c> after
    /// the push. The dismiss key is the <c>Hotkeys</c> row <c>dismissPrompt</c>. Seen prompts are kept per character by the
    /// store the factory gives for <see cref="WorldClientFeed.CharacterKey"/>. Display time is the caller's clock in ms.
    /// </summary>
    public sealed class TutorialBinder : IDisposable
    {
        private readonly ConfigSet _tables;
        private readonly HudView _view;
        private readonly TutorialPromptViewModel _model;
        private readonly Func<string, ITutorialSeenStore> _storeFor;
        private readonly int _gateHoldMs;
        private readonly Dictionary<ushort, NpcKind> _npcKinds = new Dictionary<ushort, NpcKind>();
        private readonly List<TextArg> _hotkeyArgs = new List<TextArg>();
        private readonly InputAction? _dismiss;
        private readonly string _dismissKey;
        private WorldClientFeed? _feed;
        private ClientTutorial? _tutorial;
        private double _gateUntilMs;
        private int _gateLevel;
        private string _pickupKey = "";

        /// <param name="storeFor">The seen store of a character key.</param>
        /// <param name="readDismissKey">False in tests: no Input System action is made for the dismiss key.</param>
        public TutorialBinder(ConfigSet tables, HudView view, TutorialPromptViewModel model, Func<string, ITutorialSeenStore> storeFor,
            bool readDismissKey = true)
        {
            _tables = tables;
            _view = view;
            _model = model;
            _storeFor = storeFor;
            _gateHoldMs = TutorialSettings.Find(tables)?.GatePromptHoldMs ?? 0;
            if (tables.TryGet(typeof(NpcDef), out var npcs))
            {
                foreach (var row in npcs.Rows)
                    _npcKinds[(ushort)((NpcDef)row).NetId] = ((NpcDef)row).Kind;
            }
            string dismissBinding = "";
            if (tables.TryGet(typeof(Hotkeys), out var hotkeys))
            {
                foreach (var row in hotkeys.Rows)
                {
                    var hotkey = (Hotkeys)row;
                    _hotkeyArgs.Add(new TextArg("hotkey." + hotkey.Id, OutpostBinder.KeyNameOf(hotkey.Binding)));
                    if (hotkey.Id == Hotkeys.DismissPrompt)
                        dismissBinding = hotkey.Binding;
                }
            }
            _dismissKey = OutpostBinder.KeyNameOf(dismissBinding);
            if (readDismissKey && dismissBinding.Length > 0)
            {
                _dismiss = new InputAction("DismissPrompt", InputActionType.Button, dismissBinding);
                _dismiss.Enable();
                _dismissKey = _dismiss.GetBindingDisplayString();
            }
            KeyArgs = BuildKeyArgs();
        }

        /// <summary>
        /// The key names a prompt text may name: <c>{hotkey.&lt;id&gt;}</c> for every <c>Hotkeys</c> row, and
        /// <c>{hotkey.pickup}</c> for the pickup binding (still in <c>GameInput</c>, TODO 9.6).
        /// </summary>
        public TextArg[] KeyArgs { get; private set; }

        /// <summary>The schedule of the bound character, null before its first level.</summary>
        public ClientTutorial? Tutorial => _tutorial;

        public WorldClientFeed? Feed => _feed;

        public void Bind(WorldClientFeed feed)
        {
            Unbind();
            _feed = feed;
            feed.ProgressionReceived += OnProgression;
            feed.SellAnswered += OnSold;
            feed.Disconnected += OnDisconnected;
            if (feed.Progression != null) // it may have come before the HUD bound
                OnProgression(feed.Progression);
        }

        public void Unbind()
        {
            if (_feed == null)
                return;
            _feed.ProgressionReceived -= OnProgression;
            _feed.SellAnswered -= OnSold;
            _feed.Disconnected -= OnDisconnected;
            _feed = null;
        }

        public void Dispose()
        {
            Unbind();
            _dismiss?.Dispose();
        }

        /// <summary>The player closed the scheduled prompt (its button or key); the next queued one follows.</summary>
        public void Dismiss()
        {
            if (_model.ShowsGate || _tutorial?.Current == null)
                return;
            _tutorial.Dismiss();
            Publish();
        }

        /// <summary>Once per frame: the dismiss key, the gate refusal, the schedule's triggers.</summary>
        public void Frame(double nowMs)
        {
            var feed = _feed;
            if (feed == null)
                return;
            if (_dismiss != null && _dismiss.WasPressedThisFrame() && !feed.TextInputActive)
                Dismiss();
            if (feed.PickupKeyName != _pickupKey)
            {
                _pickupKey = feed.PickupKeyName;
                KeyArgs = BuildKeyArgs();
            }

            int stop = feed.Predictor.GateStopLevel;
            if (stop > 0)
            {
                _gateLevel = stop;
                _gateUntilMs = nowMs + _gateHoldMs;
            }
            else if (_gateLevel > 0 && nowMs >= _gateUntilMs)
            {
                _gateLevel = 0;
            }

            if (_tutorial != null)
            {
                bool outpost = false, vendor = false;
                if (feed.NpcTarget != 0 && _npcKinds.TryGetValue(feed.NpcTargetDefId, out var kind))
                {
                    outpost = kind == NpcKind.Outpost;
                    vendor = kind == NpcKind.Vendor;
                }
                _tutorial.Frame(nowMs, feed.PickupTarget != 0, outpost, vendor);
            }
            Publish();
        }

        private void Publish() =>
            _model.Set(_tutorial?.Current?.Prompt ?? "", _gateLevel, _dismissKey);

        private void OnProgression(ProgressionState state)
        {
            if (_tutorial == null && _feed != null)
            {
                var key = _feed.CharacterKey;
                var store = key.Length > 0 ? _storeFor(key) : new MemoryTutorialSeenStore();
                _tutorial = ClientTutorial.From(_tables, store);
            }
            _tutorial?.SetLevel(state.Level);
            _view.ApplyLevel(state.Level); // spec 9.12: the HUD elements of the schedule appear with their level
        }

        private void OnSold(SellResult result)
        {
            if (result.Reason == OutpostReason.None)
                _tutorial?.OnItemSold();
        }

        /// <summary>The next character starts its own schedule (its own seen prompts).</summary>
        private void OnDisconnected(string reasonCode)
        {
            _tutorial = null;
            _gateLevel = 0;
            Publish();
        }

        private TextArg[] BuildKeyArgs()
        {
            var args = new TextArg[_hotkeyArgs.Count + 1];
            _hotkeyArgs.CopyTo(args);
            args[_hotkeyArgs.Count] = new TextArg("hotkey.pickup", _pickupKey);
            return args;
        }
    }
}
