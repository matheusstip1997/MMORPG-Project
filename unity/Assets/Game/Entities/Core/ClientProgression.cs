#nullable enable
using System;
using Shared.Protocol;

namespace Game.Gameplay.Entities
{
    /// <summary>
    /// The client's view of its own level, exp, rank and skill points (spec 3.6, 9.4; WP-M2-3): the last
    /// <see cref="ProgressionState"/> the server sent, with the unspent and placed free attribute points (R-3.4-08, WP-M2-13).
    /// Display only (spec 9.1): the server decides every number; a placement is a request (<see cref="Request"/>) the server
    /// answers with an <see cref="AttributeSpendResult"/> (<see cref="LastAttributeResult"/>). Engine-free, so the bots
    /// (ClientCore) read the level and place points the same way.
    /// </summary>
    public sealed class ClientProgression
    {
        /// <summary>The last state; null before the first message and after a reset.</summary>
        public ProgressionState? State { get; private set; }

        /// <summary>The last attribute point answer (WP-M2-13); null before the first one and after a reset.</summary>
        public AttributeSpendResult? LastAttributeResult { get; private set; }

        /// <summary>Attribute requests sent on this connection: the next request id (the server's idempotency key per connection).</summary>
        public uint AttributeRequestsSent { get; private set; }

        /// <summary>
        /// Takes a <see cref="ProgressionState"/> or an <see cref="AttributeSpendResult"/> of the reliable channel; false for any
        /// other message. <paramref name="log"/> says what happened in words for the dev log.
        /// </summary>
        public bool Apply(ReadOnlySpan<byte> message, out string log)
        {
            log = string.Empty;
            switch (ReliableMessages.PeekId(message))
            {
                case ReliableMessageId.ProgressionState:
                    if (!ReliableMessages.TryRead(message, out ProgressionState state))
                    {
                        log = "dropped an unreadable progression message";
                        return true;
                    }
                    State = state;
                    log = $"level {state.Level} ({state.Exp}/{state.ExpToNext} exp), rank {state.Rank}, {state.SkillPoints} skill points, "
                          + $"{state.AttributePoints} attribute points"
                          + (state.Cause == ProgressionCause.Initial || state.Cause == ProgressionCause.Exp ? string.Empty : $": {state.Cause}");
                    return true;
                case ReliableMessageId.AttributeSpendResult:
                    if (!ReliableMessages.TryRead(message, out AttributeSpendResult result))
                    {
                        log = "dropped an unreadable attribute answer";
                        return true;
                    }
                    LastAttributeResult = result;
                    log = $"attribute request {result.RequestId}: {result.Reason}";
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// The bytes of a request to place one free attribute point on <paramref name="attribute"/> (<c>Attributes.Str</c> ...
        /// <c>Attributes.Int</c>) and its <paramref name="requestId"/>, which the answer carries.
        /// </summary>
        public byte[] Request(byte attribute, out uint requestId)
        {
            requestId = ++AttributeRequestsSent;
            return ReliableMessages.Write(ReliableMessageId.AttributeSpendRequest,
                new AttributeSpendRequest { RequestId = requestId, Attribute = attribute });
        }

        /// <summary>A new connection starts over (the server sends the whole state again; request ids start again).</summary>
        public void Reset()
        {
            State = null;
            LastAttributeResult = null;
            AttributeRequestsSent = 0;
        }
    }
}
