#nullable enable
using System;
using System.Collections.Generic;
using Shared.Protocol;

namespace Game.Gameplay.Entities
{
    /// <summary>
    /// The client's side of the auction house (spec 4.5, 9.13; M4 decision 6; WP-M4-7), engine-free so the bots use it too: the
    /// listing requests with their ids (one counter per connection, shared by every listing request), the last search page
    /// (<see cref="ListingSearchResponse"/>), the player's own listings (<see cref="MyListings"/>) and the answers to create, buy
    /// and cancel (<see cref="ListingResult"/>, kept by request id). The server owns every listing and decides every sale; the
    /// client only asks, always at the auctioneer (<paramref name="npcEntityId"/> on every request).
    /// </summary>
    public sealed class ClientListings
    {
        private readonly Dictionary<uint, ListingResult> _results = new Dictionary<uint, ListingResult>();

        public ListingSearchResponse? Page { get; private set; }

        public MyListings? Mine { get; private set; }

        public ListingResult? LastResult { get; private set; }

        /// <summary>Listing requests sent on this connection; the next one gets this plus one as its id.</summary>
        public uint RequestsSent { get; private set; }

        /// <summary>Counts the messages taken and the resets (the UI redraws on a change).</summary>
        public int Revision { get; private set; }

        /// <summary>Takes the listing messages of the reliable channel; false for any other message.</summary>
        public bool Apply(ReadOnlySpan<byte> message, out string log)
        {
            log = string.Empty;
            switch (ReliableMessages.PeekId(message))
            {
                case ReliableMessageId.ListingSearchResponse:
                    if (ReliableMessages.TryRead(message, out ListingSearchResponse page))
                    {
                        Page = page;
                        Revision++;
                        log = $"listing search {page.RequestId}: {page.Reason}, {page.Rows.Length} rows, more {page.HasMore}";
                    }
                    return true;
                case ReliableMessageId.MyListings:
                    if (ReliableMessages.TryRead(message, out MyListings mine))
                    {
                        Mine = mine;
                        Revision++;
                        log = $"my listings {mine.RequestId}: {mine.Reason}, {mine.Rows.Length} of {mine.Cap}";
                    }
                    return true;
                case ReliableMessageId.ListingResult:
                    if (ReliableMessages.TryRead(message, out ListingResult result))
                    {
                        LastResult = result;
                        _results[result.RequestId] = result;
                        Revision++;
                        log = $"listing {result.Action} {result.RequestId}: {result.Reason}, listing {result.ListingId}, count {result.Count}, "
                              + $"price {result.Price}, tax {result.Tax}, fee {result.Fee}, mail {result.ToMail}";
                    }
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>A search page (see <see cref="ListingSearchRequest"/>); the filter fields are set on <paramref name="filters"/>.</summary>
        public byte[] SearchRequest(uint npcEntityId, ListingSearchRequest filters, out uint requestId)
        {
            requestId = ++RequestsSent;
            filters.RequestId = requestId;
            filters.NpcEntityId = npcEntityId;
            return ReliableMessages.Write(ReliableMessageId.ListingSearchRequest, filters);
        }

        /// <summary>The next page after the last row of <paramref name="page"/> with the same filters.</summary>
        public static ListingSearchRequest NextPage(ListingSearchRequest filters, ListingSearchResponse page)
        {
            var next = Copy(filters);
            if (page.Rows.Length > 0)
            {
                var last = page.Rows[page.Rows.Length - 1];
                next.AfterUnitPrice = last.UnitPrice;
                next.AfterListingId = last.ListingId;
            }
            return next;
        }

        public byte[] CreateRequest(uint npcEntityId, byte container, byte slot, int count, long unitPrice, int durationIndex,
            out uint requestId)
        {
            requestId = ++RequestsSent;
            return ReliableMessages.Write(ReliableMessageId.ListingCreateRequest, new ListingCreateRequest
            {
                RequestId = requestId, NpcEntityId = npcEntityId, Container = container, Slot = slot,
                Count = (ushort)Math.Max(0, Math.Min(ushort.MaxValue, count)), UnitPrice = unitPrice,
                DurationIndex = (byte)Math.Max(0, Math.Min(byte.MaxValue, durationIndex)),
            });
        }

        public byte[] BuyRequest(uint npcEntityId, long listingId, int count, long unitPrice, out uint requestId)
        {
            requestId = ++RequestsSent;
            return ReliableMessages.Write(ReliableMessageId.ListingBuyRequest, new ListingBuyRequest
            {
                RequestId = requestId, NpcEntityId = npcEntityId, ListingId = listingId,
                Count = (ushort)Math.Max(0, Math.Min(ushort.MaxValue, count)), UnitPrice = unitPrice,
            });
        }

        public byte[] CancelRequest(uint npcEntityId, long listingId, out uint requestId)
        {
            requestId = ++RequestsSent;
            return ReliableMessages.Write(ReliableMessageId.ListingCancelRequest,
                new ListingCancelRequest { RequestId = requestId, NpcEntityId = npcEntityId, ListingId = listingId });
        }

        public byte[] MineRequest(uint npcEntityId, out uint requestId)
        {
            requestId = ++RequestsSent;
            return ReliableMessages.Write(ReliableMessageId.MyListingsRequest,
                new MyListingsRequest { RequestId = requestId, NpcEntityId = npcEntityId });
        }

        /// <summary>The search page answering <paramref name="requestId"/>, or null when it has not come yet.</summary>
        public ListingSearchResponse? PageFor(uint requestId) => Page != null && Page.RequestId == requestId ? Page : null;

        /// <summary>The own listings answering <paramref name="requestId"/> (0: pushed when the auction opened), or null.</summary>
        public MyListings? MineFor(uint requestId) => Mine != null && Mine.RequestId == requestId ? Mine : null;

        /// <summary>The answer to a create, buy or cancel with <paramref name="requestId"/>, or null when it has not come yet.</summary>
        public ListingResult? ResultFor(uint requestId) => _results.TryGetValue(requestId, out var result) ? result : null;

        /// <summary>A new connection: its request ids start again.</summary>
        public void Reset()
        {
            Page = null;
            Mine = null;
            LastResult = null;
            _results.Clear();
            RequestsSent = 0;
            Revision++;
        }

        private static ListingSearchRequest Copy(ListingSearchRequest filters) => new ListingSearchRequest
        {
            ItemNetIds = (ushort[])filters.ItemNetIds.Clone(),
            Category = filters.Category,
            RarityMin = filters.RarityMin,
            RarityMax = filters.RarityMax,
            LevelMin = filters.LevelMin,
            LevelMax = filters.LevelMax,
            JobNetId = filters.JobNetId,
            ArmorTier = filters.ArmorTier,
            AfterUnitPrice = filters.AfterUnitPrice,
            AfterListingId = filters.AfterListingId,
        };
    }
}
