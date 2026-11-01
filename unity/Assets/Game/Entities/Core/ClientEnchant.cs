#nullable enable
using System;
using System.Collections.Generic;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Crafting;

namespace Game.Gameplay.Entities
{
    /// <summary>One item the enchant tab offers: where it lies, what it is, its committed level and what a try costs.</summary>
    public readonly struct EnchantCandidate
    {
        public EnchantCandidate(byte container, byte slot, long instanceId, ItemDef item, int level, ItemDef stone, int stoneCount)
        {
            Container = container;
            Slot = slot;
            InstanceId = instanceId;
            Item = item;
            Level = level;
            Stone = stone;
            StoneCount = stoneCount;
        }

        public byte Container { get; }
        public byte Slot { get; }
        public long InstanceId { get; }
        public ItemDef Item { get; }
        public int Level { get; }
        public ItemDef Stone { get; }
        public int StoneCount { get; }
        public bool Maxed => Level >= EnchantLimits.MaxLevel;
    }

    /// <summary>
    /// The client's side of enchanting (spec 5.3 R-5.3-04, 9.13; WP-M3-8), engine-free so the bots use it too: the requests
    /// with their ids (one counter per connection), the last answer (<see cref="EnchantResult"/>), the items of the
    /// character's jobs the tab offers at a station and the stones held. The server checks every request again and rolls the
    /// outcome; the client never decides a level (it shows the committed one from <c>InventoryContents</c>).
    /// </summary>
    public sealed class ClientEnchant
    {
        /// <summary>The enchant tables (from the config the client loaded); without them nothing is offered.</summary>
        public EnchantRules? Rules { get; set; }

        public EnchantResult? LastResult { get; private set; }

        /// <summary>Enchant requests sent on this connection; the next one gets this plus one as its id.</summary>
        public uint RequestsSent { get; private set; }

        /// <summary>Counts the answers taken and the resets (the UI redraws on a change).</summary>
        public int Revision { get; private set; }

        /// <summary>Takes the enchant answers of the reliable channel; false for any other message.</summary>
        public bool Apply(ReadOnlySpan<byte> message, out string log)
        {
            log = string.Empty;
            if (ReliableMessages.PeekId(message) != ReliableMessageId.EnchantResult)
                return false;
            if (ReliableMessages.TryRead(message, out EnchantResult result))
            {
                LastResult = result;
                Revision++;
                log = $"enchant result {result.RequestId}: {result.Reason}, item {result.ItemNetId} +{result.FromLevel} -> +{result.ToLevel}, "
                      + $"stones {result.StonesUsed}";
            }
            return true;
        }

        public byte[] Request(uint stationEntityId, byte container, byte slot, long instanceId, out uint requestId)
        {
            requestId = ++RequestsSent;
            return ReliableMessages.Write(ReliableMessageId.EnchantRequest, new EnchantRequest
            {
                RequestId = requestId, StationEntityId = stationEntityId, Container = container, Slot = slot, InstanceId = instanceId,
            });
        }

        /// <summary>The answer for <paramref name="requestId"/>, or null when it has not come yet.</summary>
        public EnchantResult? ResultFor(uint requestId) => LastResult != null && LastResult.RequestId == requestId ? LastResult : null;

        /// <summary>
        /// The items the tab offers at a station of kind <paramref name="station"/>: every equipment instance in the Safe bag's
        /// equipment area and Equipped whose job is held (<paramref name="holdsJob"/>) and enchants at that station, Equipped
        /// first, then slot order. Appends to <paramref name="into"/> (cleared first).
        /// </summary>
        public void Candidates(InventoryContents? contents, NpcInteraction station, Func<string, bool> holdsJob, List<EnchantCandidate> into)
        {
            into.Clear();
            if (Rules == null || contents?.Containers == null)
                return;
            Add(contents, Rules.EquippedCode, station, holdsJob, into);
            Add(contents, Rules.SafeCode, station, holdsJob, into);
        }

        /// <summary>Unreserved units of <paramref name="stone"/> in the Normal bag.</summary>
        public int StonesHeld(InventoryContents? contents, ItemDef stone)
        {
            if (Rules == null || contents == null)
                return 0;
            int held = 0;
            foreach (var entry in Slots(contents, Rules.NormalCode))
            {
                if (entry.ItemNetId == stone.NetId && !entry.Reserved)
                    held += entry.Count;
            }
            return held;
        }

        /// <summary>The connection ended: the next connection numbers its requests from 1.</summary>
        public void Reset()
        {
            LastResult = null;
            RequestsSent = 0;
            Revision++;
        }

        private void Add(InventoryContents contents, byte code, NpcInteraction station, Func<string, bool> holdsJob, List<EnchantCandidate> into)
        {
            foreach (var entry in Slots(contents, code))
            {
                if (entry.InstanceId <= 0 || Rules!.Item(entry.ItemNetId) is not { } item)
                    continue;
                var reason = Rules.Check(item, station, holdsJob, 0);
                if (reason != EnchantReason.None || !Rules.TryMaterial(item, out var stone, out int count))
                    continue;
                into.Add(new EnchantCandidate(code, entry.Slot, entry.InstanceId, item, entry.Enchant, stone, count));
            }
        }

        /// <summary>A container's slots: the Normal bag in the contents' own fields (the M1 layout), every other in <c>Containers</c>.</summary>
        private IReadOnlyList<InventorySlotEntry> Slots(InventoryContents contents, byte code)
        {
            if (code == Rules!.NormalCode)
                return contents.Slots ?? Array.Empty<InventorySlotEntry>();
            if (contents.Containers != null)
            {
                foreach (var container in contents.Containers)
                {
                    if (container.Code == code)
                        return container.Slots ?? Array.Empty<InventorySlotEntry>();
                }
            }
            return Array.Empty<InventorySlotEntry>();
        }
    }
}
