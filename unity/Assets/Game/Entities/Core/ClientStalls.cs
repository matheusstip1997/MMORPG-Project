#nullable enable
using System;
using System.Collections.Generic;
using Shared.Protocol;

namespace Game.Gameplay.Entities
{
    /// <summary>
    /// The client's side of shop stalls (spec 4.5 "M4 stalls", 9.13; M4 decision 7; WP-M4-8), engine-free so the bots use it too:
    /// the stall places of the world (the <c>stall</c> markers in ordinal order, <c>Shared.World.StallPlaces</c>), the stall
    /// requests with their ids (one counter per connection), the stall the player looks at (<see cref="StallState"/>, kept by stall
    /// entity) and the answers (<see cref="StallResult"/>, kept by request id). The server owns every stall and decides every sale;
    /// the client only asks, always in reach of the stall place (<see cref="InteractRangeCm"/>, <c>StallSettings.interactRangeCm</c>).
    /// A stall in view is a <see cref="EntityKind.ShopStall"/> entity whose definition id is its place's ordinal; its owner's name
    /// comes as its <c>EntityIdentity</c>.
    /// </summary>
    public sealed class ClientStalls
    {
        private readonly Dictionary<uint, StallResult> _results = new Dictionary<uint, StallResult>();
        private readonly Dictionary<uint, StallState> _states = new Dictionary<uint, StallState>();
        private readonly List<(int XCm, int YCm)> _places = new List<(int XCm, int YCm)>();

        /// <summary>The stall request range in cm (<c>StallSettings.interactRangeCm</c>); 0 until set.</summary>
        public int InteractRangeCm { get; private set; }

        /// <summary>The stall places by ordinal (ground cm).</summary>
        public IReadOnlyList<(int XCm, int YCm)> Places => _places;

        public StallState? LastState { get; private set; }

        public StallResult? LastResult { get; private set; }

        /// <summary>Stall requests sent on this connection; the next one gets this plus one as its id.</summary>
        public uint RequestsSent { get; private set; }

        /// <summary>Counts the messages taken and the resets (the UI redraws on a change).</summary>
        public int Revision { get; private set; }

        /// <summary>The stall places of the loaded world (ordinal order) and the request range.</summary>
        public void SetPlaces(IReadOnlyList<(int XCm, int YCm)> places, int interactRangeCm)
        {
            _places.Clear();
            _places.AddRange(places);
            InteractRangeCm = interactRangeCm;
            Revision++;
        }

        /// <summary>Takes the stall messages of the reliable channel; false for any other message.</summary>
        public bool Apply(ReadOnlySpan<byte> message, out string log)
        {
            log = string.Empty;
            switch (ReliableMessages.PeekId(message))
            {
                case ReliableMessageId.StallState:
                    if (ReliableMessages.TryRead(message, out StallState state))
                    {
                        LastState = state;
                        _states[state.StallEntityId] = state;
                        Revision++;
                        log = $"stall {state.StallEntityId} at {state.Place}: open {state.Open}, own {state.Own}, {state.Rows.Length} of {state.Cap}";
                    }
                    return true;
                case ReliableMessageId.StallResult:
                    if (ReliableMessages.TryRead(message, out StallResult result))
                    {
                        LastResult = result;
                        _results[result.RequestId] = result;
                        Revision++;
                        log = $"stall {result.Action} {result.RequestId}: {result.Reason} {result.ListingReason}, stall {result.StallEntityId}, "
                              + $"listing {result.ListingId}, count {result.Count}, price {result.Price}, tax {result.Tax}, fee {result.Fee}";
                    }
                    return true;
                default:
                    return false;
            }
        }

        public byte[] OpenRequest(int place, byte container, byte slot, int count, long unitPrice, out uint requestId) =>
            Write(new StallRequest
            {
                Action = StallAction.Open, Place = (byte)Math.Max(0, Math.Min(byte.MaxValue, place)), Container = container, Slot = slot,
                Count = Count(count), UnitPrice = unitPrice,
            }, out requestId);

        public byte[] AddRequest(uint stallEntityId, byte container, byte slot, int count, long unitPrice, out uint requestId) =>
            Write(new StallRequest
            {
                Action = StallAction.Add, StallEntityId = stallEntityId, Container = container, Slot = slot, Count = Count(count),
                UnitPrice = unitPrice,
            }, out requestId);

        public byte[] CancelRequest(uint stallEntityId, long listingId, out uint requestId) =>
            Write(new StallRequest { Action = StallAction.Cancel, StallEntityId = stallEntityId, ListingId = listingId }, out requestId);

        public byte[] CloseRequest(uint stallEntityId, out uint requestId) =>
            Write(new StallRequest { Action = StallAction.Close, StallEntityId = stallEntityId }, out requestId);

        public byte[] BrowseRequest(uint stallEntityId, out uint requestId) =>
            Write(new StallRequest { Action = StallAction.Browse, StallEntityId = stallEntityId }, out requestId);

        public byte[] LeaveRequest(uint stallEntityId, out uint requestId) =>
            Write(new StallRequest { Action = StallAction.Leave, StallEntityId = stallEntityId }, out requestId);

        public byte[] BuyRequest(uint stallEntityId, long listingId, int count, long unitPrice, out uint requestId) =>
            Write(new StallRequest
            {
                Action = StallAction.Buy, StallEntityId = stallEntityId, ListingId = listingId, Count = Count(count), UnitPrice = unitPrice,
            }, out requestId);

        /// <summary>The answer to the request <paramref name="requestId"/>, or null when it has not come yet.</summary>
        public StallResult? ResultFor(uint requestId) => _results.TryGetValue(requestId, out var result) ? result : null;

        /// <summary>The last state of the stall <paramref name="stallEntityId"/> (browsed or own), or null.</summary>
        public StallState? StateOf(uint stallEntityId) => _states.TryGetValue(stallEntityId, out var state) ? state : null;

        /// <summary>
        /// The stall the interaction key talks to: the nearest <see cref="EntityKind.ShopStall"/> whose place is within
        /// <see cref="InteractRangeCm"/> of both the predicted and the last server-confirmed own position (ground cm), the range the
        /// server checks, like <c>ClientOutposts.NearestNpc</c>; 0 for none. Allocates nothing (read every frame).
        /// </summary>
        public uint NearestStall(EntityRegistry registry, int predictedXCm, int predictedYCm, int confirmedXCm, int confirmedYCm)
        {
            uint best = 0;
            long bestSq = long.MaxValue;
            foreach (var entity in registry.EntityValues)
            {
                if (entity.Kind != EntityKind.ShopStall || entity.DefId >= _places.Count)
                    continue;
                var place = _places[entity.DefId];
                if (InReach(place, predictedXCm, predictedYCm, out long sq) && InReach(place, confirmedXCm, confirmedYCm, out _)
                    && (sq < bestSq || (sq == bestSq && entity.Id < best)))
                {
                    best = entity.Id;
                    bestSq = sq;
                }
            }
            return best;
        }

        /// <summary>
        /// The free stall place in reach (the same rule as <see cref="NearestStall"/>) with no stall in view on it, nearest first; -1
        /// for none. A place whose stall is out of view may still be taken: the server answers <c>StallPlaceTaken</c>.
        /// </summary>
        public int NearestFreePlace(EntityRegistry registry, int predictedXCm, int predictedYCm, int confirmedXCm, int confirmedYCm)
        {
            int best = -1;
            long bestSq = long.MaxValue;
            for (int i = 0; i < _places.Count; i++)
            {
                if (!InReach(_places[i], predictedXCm, predictedYCm, out long sq) || !InReach(_places[i], confirmedXCm, confirmedYCm, out _)
                    || sq >= bestSq || Taken(registry, i))
                    continue;
                best = i;
                bestSq = sq;
            }
            return best;
        }

        /// <summary>The stall entity standing on place <paramref name="ordinal"/>, or 0 when none is in view.</summary>
        public static uint StallOn(EntityRegistry registry, int ordinal)
        {
            foreach (var entity in registry.EntityValues)
            {
                if (entity.Kind == EntityKind.ShopStall && entity.DefId == ordinal)
                    return entity.Id;
            }
            return 0;
        }

        /// <summary>A new connection: its request ids start again; the stalls in view come again with the new snapshots.</summary>
        public void Reset()
        {
            LastState = null;
            LastResult = null;
            _results.Clear();
            _states.Clear();
            RequestsSent = 0;
            Revision++;
        }

        private static bool Taken(EntityRegistry registry, int ordinal) => StallOn(registry, ordinal) != 0;

        private bool InReach((int XCm, int YCm) place, int xCm, int yCm, out long sq)
        {
            long dx = place.XCm - (long)xCm, dy = place.YCm - (long)yCm;
            sq = dx * dx + dy * dy;
            return InteractRangeCm > 0 && sq <= (long)InteractRangeCm * InteractRangeCm;
        }

        private static ushort Count(int count) => (ushort)Math.Max(0, Math.Min(ushort.MaxValue, count));

        private byte[] Write(StallRequest request, out uint requestId)
        {
            requestId = ++RequestsSent;
            request.RequestId = requestId;
            return ReliableMessages.Write(ReliableMessageId.StallRequest, request);
        }
    }
}
