#nullable enable
using System;
using System.Collections.Generic;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Items;

namespace Game.Gameplay.Entities
{
    /// <summary>
    /// Where the quick-item binding is kept (spec 3.4 consumable use, 9.13; WP-M3-3): client only, per character, like the
    /// seen tutorial prompts. Holds one <c>ItemDef</c> id or nothing.
    /// </summary>
    public interface IQuickItemStore
    {
        /// <summary>The bound item id; empty when nothing is bound.</summary>
        string Load();

        void Save(string itemId);
    }

    /// <summary>A store that forgets on exit (tests, bots).</summary>
    public sealed class MemoryQuickItemStore : IQuickItemStore
    {
        private string _itemId = string.Empty;

        public string Load() => _itemId;

        public void Save(string itemId) => _itemId = itemId ?? string.Empty;
    }

    /// <summary>
    /// The client's side of consumable use (spec 3.4 "Consumable use", 9.13; WP-M3-3), engine-free so the bots use it too:
    /// the <c>ConsumableDef</c> rows by item net id, the use requests with their ids (one counter per connection: the server
    /// scopes them by connection) and their answers, one use at a time, the cooldowns as the client estimates them, and the
    /// quick-item binding (client only, per character). The local check (<see cref="Check"/>) only saves a request that
    /// would surely be refused; the server decides every use (architecture rule 1) and checks it again.
    /// </summary>
    public sealed class ClientConsumables
    {
        private readonly Dictionary<int, ConsumableDef> _byNetId = new Dictionary<int, ConsumableDef>();
        private readonly Dictionary<string, int> _netIdById = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<int, long> _readyAtMs = new Dictionary<int, long>();
        private readonly Dictionary<string, int> _startupMs = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly ConsumptionMeterSettings _meters;
        private IQuickItemStore _store = new MemoryQuickItemStore();
        private long _pendingReadyAtMs;
        private int _pendingNetId;

        /// <summary>A use with no answer after this long (a lost connection) no longer blocks the next one.</summary>
        public const long PendingTimeoutMs = 10_000;

        public ClientConsumables(ConfigSet config)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config));
            var items = new Dictionary<string, int>(StringComparer.Ordinal);
            if (config.TryGet(typeof(ItemDef), out var itemTable))
            {
                foreach (var row in itemTable.Rows)
                    items[((ItemDef)row).Id] = ((ItemDef)row).NetId;
            }
            if (config.TryGet(typeof(ConsumableDef), out var consumables))
            {
                foreach (var row in consumables.Rows)
                {
                    var def = (ConsumableDef)row;
                    if (!items.TryGetValue(def.Id, out int netId))
                        continue;
                    _byNetId[netId] = def;
                    _netIdById[def.Id] = netId;
                }
            }
            if (config.TryGet(typeof(ActionDef), out var actions))
            {
                foreach (var row in actions.Rows)
                {
                    var action = (ActionDef)row;
                    if (action.Kind == ActionKind.UseItem)
                        _startupMs[action.Id] = action.StartupMs + action.ChannelMs;
                }
            }
            _meters = config.TryGet(typeof(ConsumptionMeterSettings), out var meters) && meters.Rows.Count == 1
                ? (ConsumptionMeterSettings)meters.Rows[0]
                : new ConsumptionMeterSettings();
        }

        /// <summary>The last answer to a use request; null before the first.</summary>
        public UseItemResult? LastResult { get; private set; }

        /// <summary>Use requests sent on this connection; the next one gets this plus one as its id.</summary>
        public uint RequestsSent { get; private set; }

        /// <summary>The use waiting for its answer (0: none).</summary>
        public uint PendingRequestId { get; private set; }

        private long PendingSentAtMs { get; set; }

        /// <summary>The quick-item key's item (<c>ItemDef</c> id); empty when nothing is bound.</summary>
        public string QuickItemId { get; private set; } = string.Empty;

        public bool IsUsable(int itemNetId) => _byNetId.ContainsKey(itemNetId);

        public bool TryGet(int itemNetId, out ConsumableDef def) => _byNetId.TryGetValue(itemNetId, out def!);

        /// <summary>The item net id of a consumable id; 0 when it is none.</summary>
        public int NetIdOf(string itemId) => _netIdById.TryGetValue(itemId ?? string.Empty, out int netId) ? netId : 0;

        /// <summary>
        /// What the server would surely refuse now (spec 3.4): not a consumable, a use already waiting, its cooldown as the
        /// client estimates it, or the meters (<see cref="ConsumableRules.Check"/> on <paramref name="stats"/>, which decay on
        /// <paramref name="nowMs"/>, the display clock). <c>None</c> lets the request go; the server may still refuse it.
        /// </summary>
        public UseItemReason Check(int itemNetId, ClientStats? stats, long nowMs)
        {
            if (!_byNetId.TryGetValue(itemNetId, out var def))
                return UseItemReason.ItemNotUsable;
            if (IsWaiting(nowMs))
                return UseItemReason.UseInProgress;
            if (_readyAtMs.TryGetValue(itemNetId, out long ready) && nowMs < ready)
                return UseItemReason.OnCooldown;
            if (stats == null || !stats.HasValues)
                return UseItemReason.None;
            return ConsumableRules.Check(def, stats.ToxicityMilli(nowMs), stats.SatietyMilli(nowMs), _meters);
        }

        /// <summary>Until this display time the consumable is on its cooldown as the client estimates it (0: not).</summary>
        public long ReadyAtMs(int itemNetId) => _readyAtMs.TryGetValue(itemNetId, out long ready) ? ready : 0;

        /// <summary>A use request for the item in <paramref name="slot"/> of container <paramref name="container"/> (0: the Normal bag).</summary>
        public byte[] UseRequest(byte container, byte slot, ushort itemNetId, long nowMs, out uint requestId)
        {
            requestId = ++RequestsSent;
            PendingRequestId = requestId;
            PendingSentAtMs = nowMs;
            _pendingNetId = itemNetId;
            // The server starts the cooldown at the effect point (the action's startup after the request).
            _pendingReadyAtMs = _byNetId.TryGetValue(itemNetId, out var def)
                ? nowMs + (_startupMs.TryGetValue(def.ActionId, out int startup) ? startup : 0) + def.CooldownMs
                : 0;
            return ReliableMessages.Write(ReliableMessageId.UseItemRequest,
                new UseItemRequest { RequestId = requestId, Container = container, Slot = slot });
        }

        /// <summary>True while a use waits for its answer (and not longer than <see cref="PendingTimeoutMs"/>).</summary>
        public bool IsWaiting(long nowMs) => PendingRequestId != 0 && nowMs - PendingSentAtMs < PendingTimeoutMs;

        /// <summary>
        /// Takes a <see cref="UseItemResult"/> (true; false for any other message). An accepted use starts the cooldown the
        /// client shows. <paramref name="log"/> says what happened in words for the dev log.
        /// </summary>
        public bool Apply(ReadOnlySpan<byte> message, out string log)
        {
            log = string.Empty;
            if (ReliableMessages.PeekId(message) != ReliableMessageId.UseItemResult)
                return false;
            if (!ReliableMessages.TryRead(message, out UseItemResult result))
            {
                log = "dropped an unreadable item use answer";
                return true;
            }
            LastResult = result;
            if (result.RequestId == PendingRequestId)
            {
                if (result.Reason == UseItemReason.None && _pendingNetId != 0)
                    _readyAtMs[_pendingNetId] = _pendingReadyAtMs;
                PendingRequestId = 0;
                _pendingNetId = 0;
            }
            log = $"use {result.RequestId}: {(result.Reason == UseItemReason.None ? "used" : result.Reason.ToString())}";
            return true;
        }

        /// <summary>Reads the quick-item binding of a character from its store (at enter world).</summary>
        public void LoadQuickItem(IQuickItemStore store)
        {
            _store = store ?? new MemoryQuickItemStore();
            string id = _store.Load();
            QuickItemId = _netIdById.ContainsKey(id) ? id : string.Empty;
        }

        /// <summary>Binds the quick-item key to a consumable (false, and nothing changes, for another item).</summary>
        public bool BindQuickItem(int itemNetId)
        {
            if (!_byNetId.TryGetValue(itemNetId, out var def))
                return false;
            QuickItemId = def.Id;
            _store.Save(def.Id);
            return true;
        }

        /// <summary>
        /// The Normal bag slot the quick-item key uses: the lowest slot holding the bound consumable, and how many the bag
        /// holds in all. False when nothing is bound or none is held.
        /// </summary>
        public bool QuickSlot(InventoryContents? contents, out byte slot, out int held)
        {
            slot = 0;
            held = 0;
            int netId = NetIdOf(QuickItemId);
            if (netId == 0 || contents == null)
                return false;
            bool found = false;
            foreach (var entry in contents.Slots)
            {
                if (entry.ItemNetId != netId || entry.Count == 0)
                    continue;
                held += entry.Count;
                if (!found || entry.Slot < slot)
                    slot = entry.Slot;
                found = true;
            }
            return found;
        }

        /// <summary>A new connection starts over: request ids restart and nothing waits. The binding and cooldowns stay.</summary>
        public void Reset()
        {
            LastResult = null;
            RequestsSent = 0;
            PendingRequestId = 0;
            _pendingNetId = 0;
        }
    }
}
