#nullable enable
using System;
using System.Collections.Generic;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Crafting;

namespace Game.Gameplay.Entities
{
    /// <summary>One piece of gear the gems tab can work on: where it lies, its item and the gems in it (item ids).</summary>
    public readonly struct GemTarget
    {
        public GemTarget(byte container, InventorySlotEntry entry, ItemDef item, IReadOnlyList<string> gems)
        {
            Container = container;
            Entry = entry;
            Item = item;
            Gems = gems;
        }

        public byte Container { get; }
        public InventorySlotEntry Entry { get; }
        public ItemDef Item { get; }
        public IReadOnlyList<string> Gems { get; }

        public bool HasFreeSlot => Gems.Count < Item.GemSlots;
    }

    /// <summary>
    /// The client's side of gem socketing and removal (spec 5.3 R-5.3-05, 9.13; WP-M3-9), engine-free so the bots use it too:
    /// the gem requests with their ids (one counter per connection), the last answer (<see cref="GemResult"/>), the gear with
    /// gem slots and the cut gems the committed bag holds, and the checks of the shared <see cref="GemRules"/>. The server
    /// checks every request again and rolls the socket; the client never decides an outcome.
    /// </summary>
    public sealed class ClientGems
    {
        private readonly byte _normal;
        private readonly byte _safe;
        private readonly byte _equipped;

        /// <param name="config">The tables the client loaded; without the gem tables nothing can be socketed.</param>
        public ClientGems(ConfigSet config)
        {
            Rules = GemRules.From(config);
            _normal = (byte)(ContainerDef.Find(config, ContainerDef.NormalBag)?.Code ?? 0);
            _safe = (byte)(ContainerDef.FindReceiver(config, ItemCategory.Equipment)?.Code ?? 0);
            _equipped = (byte)(ContainerDef.FindEquipped(config)?.Code ?? 0);
        }

        public GemRules? Rules { get; }

        public byte NormalBag => _normal;

        public GemResult? LastResult { get; private set; }

        /// <summary>Gem requests sent on this connection; the next one gets this plus one as its id.</summary>
        public uint RequestsSent { get; private set; }

        /// <summary>Counts the answers taken and the resets (the UI redraws on a change).</summary>
        public int Revision { get; private set; }

        /// <summary>Takes the gem answers of the reliable channel; false for any other message.</summary>
        public bool Apply(ReadOnlySpan<byte> message, out string log)
        {
            log = string.Empty;
            if (ReliableMessages.PeekId(message) != ReliableMessageId.GemResult)
                return false;
            if (ReliableMessages.TryRead(message, out GemResult result))
            {
                LastResult = result;
                Revision++;
                log = $"gem {result.Op} {result.RequestId}: {result.Reason}, gem {result.GemItemNetId}, success {result.Success}, "
                      + $"cost {result.CostItemNetId} x{result.CostCount}";
            }
            return true;
        }

        public byte[] Socket(uint stationEntityId, in GemTarget target, byte gemSlot, out uint requestId)
        {
            requestId = ++RequestsSent;
            return ReliableMessages.Write(ReliableMessageId.GemRequest, new GemRequest
            {
                RequestId = requestId, StationEntityId = stationEntityId, Op = GemOp.Socket, Container = target.Container,
                Slot = target.Entry.Slot, InstanceId = target.Entry.InstanceId, GemSlot = gemSlot,
            });
        }

        public byte[] Remove(uint stationEntityId, in GemTarget target, byte gemIndex, out uint requestId)
        {
            requestId = ++RequestsSent;
            return ReliableMessages.Write(ReliableMessageId.GemRequest, new GemRequest
            {
                RequestId = requestId, StationEntityId = stationEntityId, Op = GemOp.Remove, Container = target.Container,
                Slot = target.Entry.Slot, InstanceId = target.Entry.InstanceId, GemIndex = gemIndex,
            });
        }

        /// <summary>The answer for <paramref name="requestId"/>, or null when it has not come yet.</summary>
        public GemResult? ResultFor(uint requestId) => LastResult != null && LastResult.RequestId == requestId ? LastResult : null;

        /// <summary>
        /// The gear with gem slots in the committed Equipped container, then the Safe bag equipment area, in slot order (worn
        /// first: what a player sockets most). Empty without contents or rules.
        /// </summary>
        public void Targets(InventoryContents? contents, List<GemTarget> targets)
        {
            targets.Clear();
            if (contents == null || Rules == null)
                return;
            AddTargets(contents, _equipped, targets);
            AddTargets(contents, _safe, targets);
        }

        /// <summary>The cut gems of the committed Normal bag (unreserved), in slot order.</summary>
        public void Gems(InventoryContents? contents, List<InventorySlotEntry> gems)
        {
            gems.Clear();
            if (contents == null || Rules == null)
                return;
            foreach (var entry in contents.Slots)
            {
                if (entry.Count > 0 && !entry.Reserved && Rules.Item(entry.ItemNetId) is { } item && Rules.Gem(item.Id) != null)
                    gems.Add(entry);
            }
        }

        /// <summary>Unreserved units of an item in the committed Normal bag (the removal cost).</summary>
        public int Held(InventoryContents? contents, string itemId)
        {
            if (contents == null || Rules?.Item(itemId) is not { } item)
                return 0;
            int count = 0;
            foreach (var entry in contents.Slots)
            {
                if (entry.ItemNetId == item.NetId && !entry.Reserved)
                    count += entry.Count;
            }
            return count;
        }

        /// <summary>What the server would surely refuse a socket of <paramref name="gem"/> into <paramref name="target"/> for.</summary>
        public GemReason CheckSocket(in GemTarget target, InventorySlotEntry? gem, int jobLevel)
        {
            if (Rules == null)
                return GemReason.GemUnavailable;
            var gemItem = gem == null ? null : Rules.Item(gem.ItemNetId);
            return Rules.CheckSocket(target.Item, target.Gems.Count, gemItem, jobLevel, gem == null || gem.Reserved ? 0 : gem.Count);
        }

        /// <summary>What the server would surely refuse a removal of gem <paramref name="index"/> for (no job tree bonus counted).</summary>
        public GemReason CheckRemove(in GemTarget target, int index, int jobLevel, InventoryContents? contents)
        {
            if (Rules == null)
                return GemReason.GemUnavailable;
            var odds = index >= 0 && index < target.Gems.Count && Rules.Gem(target.Gems[index]) is { } gem ? Rules.Odds(gem.Tier) : null;
            int held = odds == null ? 0 : Held(contents, odds.RemovalCostItemId);
            return Rules.CheckRemove(target.Item, target.Gems, index, jobLevel, held, 0);
        }

        /// <summary>The job whose service the gem's tier is (the Jeweler), or empty for no gem.</summary>
        public string JobFor(string gemItemId) => Rules?.Gem(gemItemId) is { } gem ? Rules.JobFor(gem) : string.Empty;

        /// <summary>The connection ended: the next connection numbers its requests from 1.</summary>
        public void Reset()
        {
            LastResult = null;
            RequestsSent = 0;
            Revision++;
        }

        private void AddTargets(InventoryContents contents, byte code, List<GemTarget> targets)
        {
            foreach (var container in contents.Containers)
            {
                if (container.Code != code)
                    continue;
                foreach (var entry in container.Slots)
                {
                    if (entry.InstanceId == 0 || Rules!.Item(entry.ItemNetId) is not { } item || !GemRules.IsSocketable(item))
                        continue;
                    var gems = new List<string>(entry.Gems.Length);
                    foreach (ushort netId in entry.Gems)
                    {
                        if (Rules.Item(netId) is { } gem)
                            gems.Add(gem.Id);
                    }
                    targets.Add(new GemTarget(code, entry, item, gems));
                }
            }
        }
    }
}
