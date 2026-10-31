#nullable enable
using System;
using Game.Gameplay.Entities;
using Game.UI.Config;
using Game.UI.Menus.Outpost;
using Game.UI.ViewModels.Hud;
using Game.UI.ViewModels.Outpost;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Actions;
using UnityEngine.InputSystem;

namespace Game.UI.HUD
{
    /// <summary>
    /// Harvesting on the HUD (spec 5.2, 9.3, 9.13; WP-M3-5). The interaction key (<c>Hotkeys</c> row <c>interact</c>, T) on
    /// the resource node in reach (<see cref="WorldClientFeed.NodeTarget"/>: the nearest available node of an own job) asks
    /// the world client to harvest it; an NPC in reach keeps the key (<see cref="OutpostBinder"/>), so it only harvests when
    /// no NPC is targeted. The prompt "Press T to mine" (<c>node_prompt</c>, the node's tool type's <c>UiStrings</c> row)
    /// shows at the node and hides while an action runs (spec 9.5 "channeling: movement-related prompts hidden"); the
    /// channel itself is the cast bar's. A refusal, at once or from the server, toasts its <c>ReasonCodeStrings</c> text; a
    /// committed harvest toasts the item and count (<c>hud.toast.harvested</c>). Display time is the caller's clock in ms.
    /// </summary>
    public sealed class HarvestBinder : IDisposable
    {
        private readonly ClientNodes _nodes;
        private readonly InventoryConfig _inventory;
        private readonly UiText _text;
        private readonly NpcPromptViewModel _prompt;
        private readonly ToastViewModel _toast;
        private readonly int _toastMs;
        private readonly Func<double> _now;
        private readonly InputAction? _interact;
        private readonly string _keyName;
        private WorldClientFeed? _feed;

        /// <param name="readInteractKey">False in tests: no Input System action is made for the interaction key.</param>
        public HarvestBinder(ConfigSet tables, InventoryConfig inventory, UiText text, NpcPromptViewModel prompt, ToastViewModel toast,
            int toastMs, Func<double> nowMs, bool readInteractKey = true)
        {
            _nodes = new ClientNodes(tables);
            _inventory = inventory;
            _text = text;
            _prompt = prompt;
            _toast = toast;
            _toastMs = toastMs;
            _now = nowMs;
            string binding = Hotkeys.Find(tables, Hotkeys.Interact)?.Binding ?? "";
            _keyName = OutpostBinder.KeyNameOf(binding);
            if (readInteractKey && binding.Length > 0)
            {
                _interact = new InputAction("Harvest", InputActionType.Button, binding);
                _interact.Enable();
                _keyName = _interact.GetBindingDisplayString();
            }
        }

        public WorldClientFeed? Feed => _feed;

        /// <summary>The interaction key as the player reads it ("T").</summary>
        public string KeyName => _keyName;

        /// <summary>The prompt's <c>UiStrings</c> id for a node of <paramref name="tool"/> ("hud.node.prompt.pickaxe").</summary>
        public static string PromptOf(ToolType tool) => HudTextIds.NodePromptOf(ConfigTableReader.CamelCase(tool.ToString()));

        public void Bind(WorldClientFeed feed)
        {
            Unbind();
            _feed = feed;
            feed.HarvestAnswered += OnAnswered;
        }

        public void Unbind()
        {
            if (_feed == null)
                return;
            _feed.HarvestAnswered -= OnAnswered;
            _feed = null;
        }

        public void Dispose()
        {
            Unbind();
            _interact?.Dispose();
        }

        /// <summary>Once per frame: the interaction key (ignored while the chat input has focus) and the prompt.</summary>
        public void Frame(double nowMs)
        {
            if (_feed == null)
                return;
            if (_interact != null && _interact.WasPressedThisFrame() && !_feed.TextInputActive)
                Harvest();
            uint node = _feed.NpcTarget == 0 ? _feed.NodeTarget : 0;
            bool busy = _feed.Predictor.Actions.State != ActionState.Idle;
            if (node == 0 || busy || !_nodes.TryGetNode(_feed.NodeTargetDefId, out var def))
                _prompt.Set(0, "", _keyName);
            else
                _prompt.Set(node, PromptOf(def.ToolType), _keyName);
        }

        /// <summary>The interaction key on a node in reach: harvest it (the server checks everything again).</summary>
        public void Harvest()
        {
            if (_feed?.HarvestRequests == null || _feed.NpcTarget != 0 || _feed.NodeTarget == 0)
                return;
            var reason = _feed.HarvestRequests.RequestHarvest(_feed.NodeTarget, out _);
            if (reason != HarvestReason.None)
                _toast.Show(reason.ToString(), false, _now(), _toastMs);
        }

        private void OnAnswered(HarvestResult result)
        {
            if (result.Reason != HarvestReason.None)
            {
                _toast.Show(result.Reason.ToString(), false, _now(), _toastMs);
                return;
            }
            var item = _inventory.Item(result.ItemNetId);
            string name = item != null ? _text.Get(item.Def.NameId) : "";
            _toast.ShowText(HudTextIds.ToastHarvested, "count", result.Count.ToString(), "item", name, _now(), _toastMs);
        }
    }
}
