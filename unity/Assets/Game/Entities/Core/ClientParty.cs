#nullable enable
using System;
using Shared.Protocol;

namespace Game.Gameplay.Entities
{
    /// <summary>
    /// The client's side of the party (spec 6.5 "M4 party", 9.3 <c>party_frame</c>; WP-M4-2), engine-free so the bots use it
    /// too: the party as the server last sent it (<see cref="PartyState"/>, names already through <c>DisplayIdentity</c>), the
    /// invite waiting for an answer with its local deadline, the last answer or notice (<see cref="PartyResult"/>), and the
    /// requests with their ids (one counter per connection). The server decides everything; the client only asks.
    /// </summary>
    public sealed class ClientParty
    {
        /// <summary>The last state (null before the first); a newer version replaces an older one.</summary>
        public PartyState? State { get; private set; }

        /// <summary>The invite waiting for an answer (null when none); it lapses at <see cref="InviteExpiresAtMs"/>.</summary>
        public PartyInvite? PendingInvite { get; private set; }

        /// <summary>When <see cref="PendingInvite"/> lapses, on the clock passed to <see cref="Apply"/>.</summary>
        public double InviteExpiresAtMs { get; private set; }

        /// <summary>The last answer to a request, or a notice about an own invite (request id 0).</summary>
        public PartyResult? LastResult { get; private set; }

        /// <summary>Party requests sent on this connection; the next one gets this plus one as its id.</summary>
        public uint RequestsSent { get; private set; }

        /// <summary>Counts the states, invites and answers taken and the resets (the UI redraws on a change).</summary>
        public int Revision { get; private set; }

        /// <summary>Whether the last state shows a party.</summary>
        public bool InParty => State != null && State.PartyId != 0;

        /// <summary>The member with this entity id in the last state, or null (members are keyed by entity: no character id reaches a client).</summary>
        public PartyMemberState? Member(uint entityId)
        {
            if (State == null)
                return null;
            foreach (var member in State.Members)
            {
                if (member.EntityId == entityId)
                    return member;
            }
            return null;
        }

        /// <summary>
        /// Takes the party messages of the reliable channel at <paramref name="nowMs"/> (the invite's deadline counts from it);
        /// false for any other message. <paramref name="log"/> says what happened in words for the dev log.
        /// </summary>
        public bool Apply(ReadOnlySpan<byte> message, double nowMs, out string log)
        {
            log = string.Empty;
            switch (ReliableMessages.PeekId(message))
            {
                case ReliableMessageId.PartyState:
                    if (ReliableMessages.TryRead(message, out PartyState state) && (State == null || state.Version > State.Version))
                    {
                        State = state;
                        Revision++;
                        log = state.PartyId == 0 ? "party: none" : $"party: {state.Members.Length} members";
                    }
                    return true;
                case ReliableMessageId.PartyInvite:
                    if (ReliableMessages.TryRead(message, out PartyInvite invite))
                    {
                        PendingInvite = invite;
                        InviteExpiresAtMs = nowMs + invite.TimeoutMs;
                        Revision++;
                        log = $"party: invite {invite.InviteId} from {invite.InviterName}";
                    }
                    return true;
                case ReliableMessageId.PartyResult:
                    if (ReliableMessages.TryRead(message, out PartyResult result))
                    {
                        LastResult = result;
                        Revision++;
                        log = $"party: {result.Action} {result.Reason}";
                    }
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>The pending invite, unless it lapsed at <paramref name="nowMs"/> (then it is dropped).</summary>
        public PartyInvite? Invite(double nowMs)
        {
            if (PendingInvite != null && nowMs >= InviteExpiresAtMs)
            {
                PendingInvite = null;
                Revision++;
            }
            return PendingInvite;
        }

        /// <summary>A party request; the target by entity (an invite: one in view; a kick or pass lead: a member), else by the name the player sees.</summary>
        public byte[] Request(PartyAction action, out uint requestId, uint inviteId = 0, uint targetEntityId = 0, string targetName = "")
        {
            requestId = ++RequestsSent;
            if (action == PartyAction.Accept || action == PartyAction.Decline)
            {
                PendingInvite = null; // answered: the toast closes
                Revision++;
            }
            return ReliableMessages.Write(ReliableMessageId.PartyRequest, new PartyRequest
            {
                RequestId = requestId, Action = action, InviteId = inviteId, TargetEntityId = targetEntityId,
                TargetName = targetName ?? string.Empty,
            });
        }

        /// <summary>A new connection: request ids start again; the server sends the state anew.</summary>
        public void Reset()
        {
            State = null;
            PendingInvite = null;
            LastResult = null;
            RequestsSent = 0;
            Revision++;
        }
    }
}
