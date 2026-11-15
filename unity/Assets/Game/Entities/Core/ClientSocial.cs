#nullable enable
using System;
using Shared.Protocol;

namespace Game.Gameplay.Entities
{
    /// <summary>
    /// The client's side of friends, mute and block (spec 6.5 "M4 social", 9.3 <c>social_window</c>; WP-M4-16), engine-free so the
    /// bots use it too: the social requests with their ids (one counter per connection), the last lists
    /// (<see cref="SocialState"/>) and the last answer or notice (<see cref="SocialResult"/>). Entries are known only by the
    /// per-session ref the server gave; the server owns every list and decides every request.
    /// </summary>
    public sealed class ClientSocial
    {
        /// <summary>The lists as last sent (friends with their online state, incoming requests, blocks); null before the first.</summary>
        public SocialState? State { get; private set; }

        /// <summary>The last answer or notice (request id 0).</summary>
        public SocialResult? LastResult { get; private set; }

        /// <summary>Social requests sent on this connection; the next one gets this plus one as its id.</summary>
        public uint RequestsSent { get; private set; }

        /// <summary>Counts the messages taken and the resets (the UI redraws on a change).</summary>
        public int Revision { get; private set; }

        /// <summary>Friend requests waiting for this player's answer (a badge), 0 before the first state.</summary>
        public int IncomingCount => State?.Incoming.Length ?? 0;

        /// <summary>Takes the social messages of the reliable channel; false for any other message.</summary>
        public bool Apply(ReadOnlySpan<byte> message, out string log)
        {
            log = string.Empty;
            switch (ReliableMessages.PeekId(message))
            {
                case ReliableMessageId.SocialState:
                    if (ReliableMessages.TryRead(message, out SocialState state) && (State == null || state.Version > State.Version))
                    {
                        State = state;
                        Revision++;
                        log = $"social state {state.Version}: {state.Friends.Length} friends, {state.Incoming.Length} requests, "
                              + $"{state.Blocks.Length} blocked";
                    }
                    return true;
                case ReliableMessageId.SocialResult:
                    if (ReliableMessages.TryRead(message, out SocialResult result))
                    {
                        LastResult = result;
                        Revision++;
                        log = $"social {result.Action} {result.RequestId}: {result.Reason} {result.Notice} {result.Name}";
                    }
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>One social action: an entry of the own lists by <paramref name="reference"/>, else <paramref name="name"/>.</summary>
        public byte[] Request(SocialAction action, uint reference, string name, out uint requestId)
        {
            requestId = ++RequestsSent;
            return ReliableMessages.Write(ReliableMessageId.SocialRequest,
                new SocialRequest { RequestId = requestId, Action = action, Ref = reference, Name = name ?? string.Empty });
        }

        /// <summary>The answer to <paramref name="requestId"/>, or null when it has not come yet (or a later message replaced it).</summary>
        public SocialResult? ResultFor(uint requestId) => LastResult != null && LastResult.RequestId == requestId ? LastResult : null;

        /// <summary>The friend named <paramref name="name"/> (ignoring case) in the last state, or null.</summary>
        public SocialFriendEntry? FindFriend(string name)
        {
            if (State == null)
                return null;
            foreach (var friend in State.Friends)
            {
                if (string.Equals(friend.Name, name, StringComparison.OrdinalIgnoreCase))
                    return friend;
            }
            return null;
        }

        /// <summary>A new connection: its request ids start again; the lists come with the enter world.</summary>
        public void Reset()
        {
            State = null;
            LastResult = null;
            RequestsSent = 0;
            Revision++;
        }
    }
}
