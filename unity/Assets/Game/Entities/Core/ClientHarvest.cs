#nullable enable
using System;
using System.Collections.Generic;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;

namespace Game.Gameplay.Entities
{
    /// <summary>
    /// Harvesting on the client (spec 5.2, 9.13; WP-M3-5): what the server would surely refuse before a request goes out (the
    /// node's job, a tool of its type and tier in the Safe bag's equipment area, a harvest already waiting), the
    /// <see cref="HarvestRequest"/> itself and its <see cref="HarvestResult"/>. The channel plays on the shared action machine
    /// (the caller asks the predictor for the node's harvest action), so the cast bar follows it. Engine-free: the Unity
    /// client and the bots compile it.
    /// </summary>
    public sealed class ClientHarvest
    {
        /// <summary>A request with no answer this long no longer blocks the next one (the answer waits for a save, about 1 s).</summary>
        public const long PendingTimeoutMs = 15_000;

        private readonly Dictionary<int, ToolDef> _toolsByItemNetId = new Dictionary<int, ToolDef>();
        private readonly byte _toolContainer;

        public ClientHarvest(ConfigSet config)
        {
            var items = new Dictionary<string, int>(StringComparer.Ordinal);
            if (config.TryGet(typeof(ItemDef), out var itemTable))
            {
                foreach (var row in itemTable.Rows)
                    items[((ItemDef)row).Id] = ((ItemDef)row).NetId;
            }
            if (config.TryGet(typeof(ToolDef), out var tools))
            {
                foreach (var row in tools.Rows)
                {
                    var tool = (ToolDef)row;
                    if (items.TryGetValue(tool.ItemId, out int netId))
                        _toolsByItemNetId[netId] = tool;
                }
            }
            if (config.TryGet(typeof(ContainerDef), out var containers))
            {
                foreach (var row in containers.Rows)
                {
                    var container = (ContainerDef)row;
                    if (container.Id == ContainerDef.SafeEquipment)
                        _toolContainer = (byte)container.Code;
                }
            }
        }

        /// <summary>The last answer to a harvest request; null before the first.</summary>
        public HarvestResult? LastResult { get; private set; }

        /// <summary>Harvest requests sent on this connection; the next one gets this plus one as its id.</summary>
        public uint RequestsSent { get; private set; }

        /// <summary>The harvest waiting for its answer (0: none).</summary>
        public uint PendingRequestId { get; private set; }

        /// <summary>The node of the harvest waiting for its answer (0: none).</summary>
        public uint PendingNodeEntityId { get; private set; }

        private long PendingSentAtMs { get; set; }

        /// <summary>True while a harvest waits for its answer (and not longer than <see cref="PendingTimeoutMs"/>).</summary>
        public bool IsWaiting(long nowMs) => PendingRequestId != 0 && nowMs - PendingSentAtMs < PendingTimeoutMs;

        /// <summary>
        /// The best tier of a tool of <paramref name="type"/> in the Safe bag's equipment area of <paramref name="contents"/>
        /// (R-5.2-02: tools count there), 0 for none.
        /// </summary>
        public int BestToolTier(InventoryContents? contents, ToolType type)
        {
            if (contents == null)
                return 0;
            int best = 0;
            foreach (var container in contents.Containers)
            {
                if (container.Code != _toolContainer)
                    continue;
                foreach (var slot in container.Slots)
                {
                    if (slot.Count > 0 && _toolsByItemNetId.TryGetValue(slot.ItemNetId, out var tool) && tool.ToolType == type
                        && tool.Tier > best)
                        best = tool.Tier;
                }
            }
            return best;
        }

        /// <summary>
        /// What the server would surely refuse now for <paramref name="node"/> (spec 5.2): not the own job, no tool of its
        /// type or one below its tier, a harvest already waiting. <c>None</c> lets the request go; the server checks the
        /// reach, the bag and the node again and may still refuse it.
        /// </summary>
        public HarvestReason Check(NodeDef node, IOwnJobs jobs, InventoryContents? contents, long nowMs)
        {
            if (!jobs.Has(node.JobId))
                return HarvestReason.JobRequired;
            int tier = BestToolTier(contents, node.ToolType);
            if (tier <= 0)
                return HarvestReason.ToolRequired;
            if (tier < node.MinToolTier)
                return HarvestReason.ToolTierTooLow;
            if (IsWaiting(nowMs))
                return HarvestReason.ActionBusy;
            return HarvestReason.None;
        }

        /// <summary>A harvest request for the node entity <paramref name="nodeEntityId"/>.</summary>
        public byte[] Request(uint nodeEntityId, long nowMs, out uint requestId)
        {
            requestId = ++RequestsSent;
            PendingRequestId = requestId;
            PendingNodeEntityId = nodeEntityId;
            PendingSentAtMs = nowMs;
            return ReliableMessages.Write(ReliableMessageId.HarvestRequest,
                new HarvestRequest { RequestId = requestId, NodeEntityId = nodeEntityId });
        }

        /// <summary>
        /// Takes a <see cref="HarvestResult"/> (true; false for any other message). <paramref name="log"/> says what happened
        /// in words for the dev log.
        /// </summary>
        public bool Apply(ReadOnlySpan<byte> message, out string log)
        {
            log = string.Empty;
            if (ReliableMessages.PeekId(message) != ReliableMessageId.HarvestResult)
                return false;
            if (!ReliableMessages.TryRead(message, out HarvestResult result))
            {
                log = "dropped an unreadable harvest answer";
                return true;
            }
            LastResult = result;
            if (result.RequestId == PendingRequestId)
            {
                PendingRequestId = 0;
                PendingNodeEntityId = 0;
            }
            log = result.Reason == HarvestReason.None
                ? $"harvest {result.RequestId}: {result.Count} x item {result.ItemNetId} from node {result.NodeEntityId}"
                : $"harvest {result.RequestId}: {result.Reason}";
            return true;
        }

        /// <summary>A new connection starts over: request ids restart and nothing waits.</summary>
        public void Reset()
        {
            LastResult = null;
            RequestsSent = 0;
            PendingRequestId = 0;
            PendingNodeEntityId = 0;
        }
    }
}
