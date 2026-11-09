#nullable enable
using System;
using Shared.Protocol;

namespace Game.Gameplay.Entities
{
    /// <summary>
    /// The client's side of the trade window (spec 4.5 "M4 trade window", 9.13; WP-M4-4), engine-free so the bots use it too:
    /// the trade as the server last described it (<see cref="State"/>), the answers to the requests (<see cref="LastResult"/>),
    /// and the request builders, each carrying the revision the player saw (a lock or confirm of an older revision is refused
    /// <c>TradeChanged</c>). The server checks everything again and decides every outcome.
    /// </summary>
    public sealed class ClientTrade
    {
        /// <summary>The newest trade state; null before any trade or after <see cref="Reset"/>.</summary>
        public TradeState? State { get; private set; }

        public TradeResult? LastResult { get; private set; }

        /// <summary>Trade requests sent on this connection; the next one gets this plus one as its id.</summary>
        public uint RequestsSent { get; private set; }

        /// <summary>Counts the messages taken and the resets (the UI redraws on a change).</summary>
        public int Revision { get; private set; }

        /// <summary>The partner invited this player and waits for an answer (the invite prompt).</summary>
        public bool IsInvited => State is { Phase: TradePhase.Invited };

        /// <summary>The window is open (offers, locks, confirms) or the swap is saving.</summary>
        public bool IsOpen => State is { Phase: TradePhase.Open or TradePhase.Settling };

        /// <summary>A trade or invite is under way for this player.</summary>
        public bool IsActive => State is { Phase: TradePhase.Inviting or TradePhase.Invited or TradePhase.Open or TradePhase.Settling };

        /// <summary>Takes the trade messages of the reliable channel; false for any other message.</summary>
        public bool Apply(ReadOnlySpan<byte> message, out string log)
        {
            log = string.Empty;
            switch (ReliableMessages.PeekId(message))
            {
                case ReliableMessageId.TradeState:
                    if (ReliableMessages.TryRead(message, out TradeState state))
                    {
                        State = state;
                        Revision++;
                        log = $"trade {state.TradeId} r{state.Revision} {state.Phase} with {state.PartnerName}: own {state.OwnOffer.Length} items "
                              + $"{state.OwnGold} gold{(state.OwnLocked ? " locked" : "")}{(state.OwnConfirmed ? " confirmed" : "")}, partner "
                              + $"{state.PartnerOffer.Length} items {state.PartnerGold} gold{(state.PartnerLocked ? " locked" : "")}"
                              + $"{(state.PartnerConfirmed ? " confirmed" : "")}{(state.Reason != TradeReason.None ? ", " + state.Reason : "")}";
                    }
                    return true;
                case ReliableMessageId.TradeResult:
                    if (ReliableMessages.TryRead(message, out TradeResult result))
                    {
                        LastResult = result;
                        Revision++;
                        log = $"trade {result.Action} {result.RequestId}: {result.Reason}";
                    }
                    return true;
                default:
                    return false;
            }
        }

        public byte[] Invite(uint targetEntityId, out uint requestId) =>
            Request(new TradeRequest { Action = TradeAction.Invite, TargetEntityId = targetEntityId }, out requestId);

        public byte[] Accept(out uint requestId) => Request(new TradeRequest { Action = TradeAction.Accept }, out requestId);

        public byte[] Decline(out uint requestId) => Request(new TradeRequest { Action = TradeAction.Decline }, out requestId);

        /// <summary>Offers <paramref name="count"/> units of a container slot (1 for an instance).</summary>
        public byte[] Add(byte container, ushort slot, ushort count, out uint requestId) =>
            Request(new TradeRequest { Action = TradeAction.Add, Container = container, Slot = slot, Count = count }, out requestId);

        public byte[] Remove(byte offerIndex, out uint requestId) =>
            Request(new TradeRequest { Action = TradeAction.Remove, OfferIndex = offerIndex }, out requestId);

        public byte[] SetGold(long gold, out uint requestId) =>
            Request(new TradeRequest { Action = TradeAction.SetGold, Gold = gold }, out requestId);

        /// <summary>Locks the offers as this client last saw them (<see cref="TradeState.Revision"/>).</summary>
        public byte[] Lock(out uint requestId) => Request(new TradeRequest { Action = TradeAction.Lock }, out requestId);

        /// <summary>Confirms the offers as this client last saw them, after both sides locked.</summary>
        public byte[] Confirm(out uint requestId) => Request(new TradeRequest { Action = TradeAction.Confirm }, out requestId);

        public byte[] Cancel(out uint requestId) => Request(new TradeRequest { Action = TradeAction.Cancel }, out requestId);

        /// <summary>The answer for <paramref name="requestId"/>, or null when it has not come yet.</summary>
        public TradeResult? ResultFor(uint requestId) => LastResult != null && LastResult.RequestId == requestId ? LastResult : null;

        /// <summary>Units of <paramref name="itemNetId"/> in an offer list.</summary>
        public static int Units(TradeOffer[] offers, ushort itemNetId)
        {
            int units = 0;
            foreach (var offer in offers)
            {
                if (offer.ItemNetId == itemNetId)
                    units += offer.Count;
            }
            return units;
        }

        /// <summary>The connection ended: the next connection numbers its requests from 1 and no trade is known.</summary>
        public void Reset()
        {
            State = null;
            LastResult = null;
            RequestsSent = 0;
            Revision++;
        }

        private byte[] Request(TradeRequest request, out uint requestId)
        {
            requestId = ++RequestsSent;
            request.RequestId = requestId;
            request.Revision = State?.Revision ?? 0;
            return ReliableMessages.Write(ReliableMessageId.TradeRequest, request);
        }
    }
}
