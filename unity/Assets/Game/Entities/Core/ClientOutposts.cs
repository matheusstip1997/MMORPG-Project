#nullable enable
using System;
using System.Collections.Generic;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;

namespace Game.Gameplay.Entities
{
    /// <summary>
    /// The client's side of outposts and vendors (spec 4.2, 9.13; WP-M2-4), engine-free so the bots use it too: the NPCs
    /// the tables define (by <c>NpcDef.netId</c>, what an NPC's spawn event carries), which NPC the interaction key talks
    /// to, the outpost and vendor requests with their ids (one counter per connection: the server scopes them by
    /// connection), and the answers and the committed gold the server sends. The server checks everything again.
    /// </summary>
    public sealed class ClientOutposts
    {
        private readonly Dictionary<ushort, NpcDef> _npcs = new Dictionary<ushort, NpcDef>();

        public ClientOutposts(ConfigSet config)
        {
            if (config.TryGet(typeof(NpcDef), out var table))
            {
                foreach (var row in table.Rows)
                {
                    var npc = (NpcDef)row;
                    _npcs[(ushort)npc.NetId] = npc;
                }
            }
        }

        /// <summary>The committed gold; null before the server sent it.</summary>
        public long? Gold { get; private set; }

        /// <summary>Outpost and vendor requests sent on this connection; the next one gets this plus one as its id.</summary>
        public uint RequestsSent { get; private set; }

        public OutpostInteractResult? LastInteract { get; private set; }
        public SellQuoteResult? LastQuote { get; private set; }
        public SellResult? LastSale { get; private set; }
        public VendorBuyResult? LastPurchase { get; private set; }

        /// <summary>The NPC definition of a spawn's definition id; false for an unknown one.</summary>
        public bool TryGetNpc(ushort defId, out NpcDef npc) => _npcs.TryGetValue(defId, out npc!);

        /// <summary>
        /// Takes the outpost messages of the reliable channel; false for any other message. <paramref name="log"/> says what
        /// happened in words for the dev log.
        /// </summary>
        public bool Apply(ReadOnlySpan<byte> message, out string log)
        {
            log = string.Empty;
            switch (ReliableMessages.PeekId(message))
            {
                case ReliableMessageId.OutpostInteractResult:
                    if (ReliableMessages.TryRead(message, out OutpostInteractResult interact))
                    {
                        LastInteract = interact;
                        log = $"npc {interact.NpcEntityId} {interact.Kind}: {interact.Reason}";
                    }
                    return true;
                case ReliableMessageId.SellQuoteResult:
                    if (ReliableMessages.TryRead(message, out SellQuoteResult quote))
                    {
                        LastQuote = quote;
                        log = $"quote {quote.RequestId}: {quote.Reason} {quote.Count} for {quote.Total} ({quote.FirstUnit}..{quote.LastUnit} each)";
                    }
                    return true;
                case ReliableMessageId.SellResult:
                    if (ReliableMessages.TryRead(message, out SellResult sale))
                    {
                        LastSale = sale;
                        log = $"sale {sale.RequestId}: {sale.Reason} {sale.Count} for {sale.Gold}";
                    }
                    return true;
                case ReliableMessageId.VendorBuyResult:
                    if (ReliableMessages.TryRead(message, out VendorBuyResult purchase))
                    {
                        LastPurchase = purchase;
                        log = $"purchase {purchase.RequestId}: {purchase.Reason} {purchase.Count} of {purchase.ItemNetId} for {purchase.GoldSpent}";
                    }
                    return true;
                case ReliableMessageId.GoldBalance:
                    if (ReliableMessages.TryRead(message, out GoldBalance balance))
                    {
                        Gold = balance.Gold;
                        log = $"gold {balance.Gold}";
                    }
                    return true;
                default:
                    return false;
            }
        }

        public byte[] InteractRequest(uint npcEntityId, NpcInteraction kind, out uint requestId)
        {
            requestId = ++RequestsSent;
            return ReliableMessages.Write(ReliableMessageId.OutpostInteractRequest,
                new OutpostInteractRequest { RequestId = requestId, NpcEntityId = npcEntityId, Kind = kind });
        }

        public byte[] QuoteRequest(uint npcEntityId, byte slot, ushort count, out uint requestId)
        {
            requestId = ++RequestsSent;
            return ReliableMessages.Write(ReliableMessageId.SellQuoteRequest,
                new SellQuoteRequest { RequestId = requestId, NpcEntityId = npcEntityId, Slot = slot, Count = count });
        }

        public byte[] SellRequest(uint npcEntityId, byte slot, ushort count, long quotedTotal, out uint requestId)
        {
            requestId = ++RequestsSent;
            return ReliableMessages.Write(ReliableMessageId.SellRequest, new SellRequest
            {
                RequestId = requestId, NpcEntityId = npcEntityId, Slot = slot, Count = count, QuotedTotal = quotedTotal,
            });
        }

        public byte[] BuyRequest(uint npcEntityId, ushort itemNetId, ushort count, long quotedTotal, out uint requestId)
        {
            requestId = ++RequestsSent;
            return ReliableMessages.Write(ReliableMessageId.VendorBuyRequest, new VendorBuyRequest
            {
                RequestId = requestId, NpcEntityId = npcEntityId, ItemNetId = itemNetId, Count = count, QuotedTotal = quotedTotal,
            });
        }

        /// <summary>The connection ended: the gold shown is stale and the next connection numbers its requests from 1 again.</summary>
        public void Reset()
        {
            Gold = null;
            RequestsSent = 0;
            LastInteract = null;
            LastQuote = null;
            LastSale = null;
            LastPurchase = null;
        }

        /// <summary>
        /// The NPC the interaction key talks to and the HUD's prompt names: the nearest NPC (from the predicted position)
        /// within its <c>NpcDef.interactRangeCm</c>, the range the server checks, of both the predicted and the last
        /// server-confirmed own position (ground cm), like <see cref="ClientInventory.NearestDrop"/>; 0 for none.
        /// Allocates nothing (read every frame).
        /// </summary>
        public uint NearestNpc(EntityRegistry registry, int predictedXCm, int predictedYCm, int confirmedXCm, int confirmedYCm)
        {
            uint best = 0;
            long bestSq = long.MaxValue;
            foreach (var entity in registry.EntityValues)
            {
                if (entity.Kind != EntityKind.Npc || !_npcs.TryGetValue(entity.DefId, out var npc) || !entity.Samples.TryNewest(out var sample))
                    continue;
                long rangeSq = (long)npc.InteractRangeCm * npc.InteractRangeCm;
                // Snapshot axes: X is ground X, Z is ground Y (SnapshotAxes).
                long dx = sample.XCm - predictedXCm, dy = sample.ZCm - predictedYCm;
                long sq = dx * dx + dy * dy;
                long cx = sample.XCm - confirmedXCm, cy = sample.ZCm - confirmedYCm;
                if (sq <= rangeSq && cx * cx + cy * cy <= rangeSq && (sq < bestSq || (sq == bestSq && entity.Id < best)))
                {
                    best = entity.Id;
                    bestSq = sq;
                }
            }
            return best;
        }
    }
}
