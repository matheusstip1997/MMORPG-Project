#nullable enable
using System;
using Shared.Protocol;

namespace Game.Gameplay.Entities
{
    /// <summary>
    /// The client's side of the Bystial book (spec 14.2, 9.13; WP-M2-8), engine-free so the bots use it too: the last
    /// <see cref="BookState"/> the server sent (the collection with its counters, the slots), the change requests with their
    /// ids (one counter per connection: the server scopes them by connection) and their answers. Display only (spec 9.1):
    /// the server decides every card, counter and slot, and checks every request again.
    /// </summary>
    public sealed class ClientBook
    {
        /// <summary>The last book; null before the first message and after a reset.</summary>
        public BookState? State { get; private set; }

        /// <summary>The last answer to a change request; null before the first.</summary>
        public BookChangeResult? LastResult { get; private set; }

        /// <summary>Change requests sent on this connection; the next one gets this plus one as its id.</summary>
        public uint RequestsSent { get; private set; }

        /// <summary>
        /// Takes the book messages of the reliable channel; false for any other message. <paramref name="log"/> says what
        /// happened in words for the dev log.
        /// </summary>
        public bool Apply(ReadOnlySpan<byte> message, out string log)
        {
            log = string.Empty;
            switch (ReliableMessages.PeekId(message))
            {
                case ReliableMessageId.BookState:
                    if (ReliableMessages.TryRead(message, out BookState state))
                    {
                        State = state;
                        log = $"book {state.Version}: {state.Cards.Length} monster types, {state.SlotCount} slots" +
                              (state.Cause == BookCause.Initial || state.Cause == BookCause.Kills ? string.Empty : $": {state.Cause}");
                    }
                    else
                    {
                        log = "dropped an unreadable book state";
                    }
                    return true;
                case ReliableMessageId.BookChangeResult:
                    if (ReliableMessages.TryRead(message, out BookChangeResult result))
                    {
                        LastResult = result;
                        log = $"book change {result.RequestId}: {result.Reason}";
                    }
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// A change request: put the card of <paramref name="monsterNetId"/> into <paramref name="slot"/> (an occupied slot
        /// swaps), into the first free slot (<see cref="BookLimits.AnyFreeSlot"/>) or take it out (<see cref="BookLimits.NoSlot"/>).
        /// </summary>
        public byte[] ChangeRequest(ushort monsterNetId, byte slot, out uint requestId)
        {
            requestId = ++RequestsSent;
            return ReliableMessages.Write(ReliableMessageId.BookChangeRequest,
                new BookChangeRequest { RequestId = requestId, MonsterNetId = monsterNetId, Slot = slot });
        }

        /// <summary>A new connection starts over: the server sends the whole book again and request ids restart.</summary>
        public void Reset()
        {
            State = null;
            LastResult = null;
            RequestsSent = 0;
        }
    }
}
