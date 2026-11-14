#nullable enable
using System;
using Shared.Protocol;

namespace Game.Gameplay.Entities
{
    /// <summary>
    /// The client's side of death loot bags (spec 4.3 R-4.3-03, 9.12; WP-M4-9), engine-free so the bots use it too: the bag
    /// requests with their ids (one counter per connection), the bag open now (<see cref="LootBagContent"/>), the last answer
    /// (<see cref="LootBagResult"/>) and the player's own bags at any distance (<see cref="OwnLootBags"/>, the minimap marker
    /// and the run back). The server owns every bag and decides every take; the client only asks.
    /// </summary>
    public sealed class ClientLootBags
    {
        /// <summary>The bag open now, or null (closed, gone, out of reach).</summary>
        public LootBagContent? Open { get; private set; }

        public LootBagResult? LastResult { get; private set; }

        /// <summary>The player's own bags, newest first; null until the server sent them (at enter world).</summary>
        public OwnLootBags? Own { get; private set; }

        /// <summary>Loot bag requests sent on this connection; the next one gets this plus one as its id.</summary>
        public uint RequestsSent { get; private set; }

        /// <summary>Counts the messages taken and the resets (the UI redraws on a change).</summary>
        public int Revision { get; private set; }

        /// <summary>The own bags' list grew since the last call (a death dropped one): the "your bag dropped" prompt.</summary>
        public bool TakeNewOwnBag()
        {
            bool grew = _ownGrew;
            _ownGrew = false;
            return grew;
        }

        private bool _ownGrew;
        private readonly System.Collections.Generic.HashSet<uint> _used = new System.Collections.Generic.HashSet<uint>();

        /// <summary>A step has dealt with this own bag (reported it, or taken it back): the bots' die step looks for newer ones.</summary>
        public void MarkUsed(uint bagEntityId) => _used.Add(bagEntityId);

        public bool IsUsed(uint bagEntityId) => _used.Contains(bagEntityId);

        /// <summary>The newest own bag, or null.</summary>
        public OwnLootBagEntry? NewestOwn => Own != null && Own.Bags.Length > 0 ? Own.Bags[0] : null;

        /// <summary>Takes the loot bag messages of the reliable channel; false for any other message.</summary>
        public bool Apply(ReadOnlySpan<byte> message, out string log)
        {
            log = string.Empty;
            switch (ReliableMessages.PeekId(message))
            {
                case ReliableMessageId.LootBagContent:
                    if (ReliableMessages.TryRead(message, out LootBagContent content))
                    {
                        Open = content.Closed ? null : content;
                        Revision++;
                        log = content.Closed
                            ? $"loot bag {content.BagEntityId} closed"
                            : $"loot bag {content.BagEntityId}: {content.Access}, {content.Rows.Length} rows, window {content.OwnerWindowLeftMs} ms, "
                              + $"expires in {content.ExpiresInMs} ms";
                    }
                    return true;
                case ReliableMessageId.LootBagResult:
                    if (ReliableMessages.TryRead(message, out LootBagResult result))
                    {
                        LastResult = result;
                        Revision++;
                        log = $"loot bag {result.Op} {result.RequestId}: {result.Reason}, bag {result.BagEntityId}, taken {result.Taken}, "
                              + $"mailed {result.Mailed}{(result.Stolen ? ", stolen" : "")}";
                    }
                    return true;
                case ReliableMessageId.OwnLootBags:
                    if (ReliableMessages.TryRead(message, out OwnLootBags own))
                    {
                        if (own.Bags.Length > 0 && (Own == null || !Contains(Own, own.Bags[0].BagEntityId)))
                            _ownGrew = Own != null; // the first list of a connection is not a new drop
                        Own = own;
                        Revision++;
                        log = $"own loot bags: {own.Bags.Length}";
                    }
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Asks to open the bag <paramref name="bagEntityId"/> (its contents follow while it stays open and in reach).</summary>
        public byte[] OpenRequest(uint bagEntityId, out uint requestId) => Request(bagEntityId, LootBagOp.Open, 0, out requestId);

        /// <summary>Asks to take one row of the bag.</summary>
        public byte[] TakeOneRequest(uint bagEntityId, byte row, out uint requestId) => Request(bagEntityId, LootBagOp.TakeOne, row, out requestId);

        /// <summary>Asks to take every row of the bag.</summary>
        public byte[] TakeAllRequest(uint bagEntityId, out uint requestId) => Request(bagEntityId, LootBagOp.TakeAll, 0, out requestId);

        public byte[] CloseRequest(uint bagEntityId, out uint requestId) => Request(bagEntityId, LootBagOp.Close, 0, out requestId);

        /// <summary>The answer for <paramref name="requestId"/>, or null when it has not come yet.</summary>
        public LootBagResult? ResultFor(uint requestId) => LastResult != null && LastResult.RequestId == requestId ? LastResult : null;

        /// <summary>The own bag shown by <paramref name="bagEntityId"/>, or null.</summary>
        public OwnLootBagEntry? OwnBag(uint bagEntityId)
        {
            if (Own == null)
                return null;
            foreach (var bag in Own.Bags)
            {
                if (bag.BagEntityId == bagEntityId)
                    return bag;
            }
            return null;
        }

        /// <summary>A new connection: its request ids start again; the own bags come with the enter world.</summary>
        public void Reset()
        {
            Open = null;
            LastResult = null;
            Own = null;
            RequestsSent = 0;
            _ownGrew = false;
            _used.Clear(); // entity ids are a connection's view; a bag seen again on a new connection is new to it
            Revision++;
        }

        private byte[] Request(uint bagEntityId, LootBagOp op, byte row, out uint requestId)
        {
            requestId = ++RequestsSent;
            return ReliableMessages.Write(ReliableMessageId.LootBagRequest,
                new LootBagRequest { RequestId = requestId, BagEntityId = bagEntityId, Op = op, Row = row });
        }

        private static bool Contains(OwnLootBags bags, uint bagEntityId)
        {
            foreach (var bag in bags.Bags)
            {
                if (bag.BagEntityId == bagEntityId)
                    return true;
            }
            return false;
        }
    }
}
