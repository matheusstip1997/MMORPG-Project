using GameServer.Entities;
using GameServer.Events;
using Shared.Data.Tables;
using Shared.Protocol;

namespace GameServer.Social;

/// <summary>
/// The parties of the world (spec 6.5 "M4 party", R-6.5-01; M4 decision 1; WP-M4-2): <b>the only writer of party state</b>,
/// in memory on the world thread (one world in M4; nothing is persisted). A party has up to
/// <see cref="PartySettings.MaxMembers"/> members in join order and one leader, who invites, kicks and passes the lead; a
/// member leaves at will; the party dissolves below two members. An invite waits <see cref="PartySettings.InviteTimeoutMs"/>
/// (one pending invite per invitee); the party forms on the first accept, the inviter its leader. A disconnected member stays
/// while its character lingers (R-2.1-08) and leaves when the world removes the character (<see cref="Detach"/>). No roles in
/// M4. Requests run in phase 1 in entity-id order, so races in one tick resolve the same way every time (rule 6); expired
/// invites are swept by time (<see cref="Sweep"/>), never by timers. Answers go to the players' <see cref="PartyAnswers"/>.
/// </summary>
public sealed class PartyService : IPartyQuery, IPartyLootDebts
{
    private readonly PartySettings _settings;
    private readonly EventBus? _events;
    private readonly Dictionary<long, Party> _parties = new();
    private readonly Dictionary<long, Party> _byMember = new();
    private readonly List<PendingInvite> _invites = new(); // in invite id order
    private readonly Dictionary<long, uint> _expired = new(); // invitee -> its last invite that timed out
    private long _nextPartyId;
    private uint _nextInviteId;

    public PartyService(PartySettings settings, EventBus? events)
    {
        _settings = settings;
        _events = events;
    }

    public PartySettings Settings => _settings;

    /// <summary>The world's player of a character (online or lingering); null when it is not in the world.</summary>
    public Func<long, Player?> PlayerOf { get; set; } = _ => null;

    /// <summary>The connected player whose display name, as <c>observer</c> sees it, is the name (case-insensitive); null for none.</summary>
    public Func<Player, string, Player?> FindByName { get; set; } = (_, _) => null;

    /// <summary>A connected player the observer has in interest by entity id (rule 9: nothing the observer cannot see).</summary>
    public Func<Player, uint, Player?> FindVisible { get; set; } = (_, _) => null;

    /// <summary>Parties formed since the world started (telemetry and tests).</summary>
    public int PartyCount => _parties.Count;

    /// <summary>Invites waiting for an answer.</summary>
    public int PendingInvites => _invites.Count;

    // ---- IPartyQuery ----------------------------------------------------------------------------------------------

    public long PartyIdOf(long characterId) => _byMember.TryGetValue(characterId, out var party) ? party.Id : 0;

    public bool SameParty(long characterA, long characterB) =>
        characterA != 0 && characterA != characterB && _byMember.TryGetValue(characterA, out var party)
        && _byMember.TryGetValue(characterB, out var other) && party == other;

    public long LeaderOf(long partyId) => _parties.TryGetValue(partyId, out var party) ? party.Leader : 0;

    public int MembersOf(long partyId, Span<long> into)
    {
        if (!_parties.TryGetValue(partyId, out var party))
            return 0;
        int count = Math.Min(party.Members.Count, into.Length);
        for (int i = 0; i < count; i++)
            into[i] = party.Members[i];
        return count;
    }

    public int MemberCount(long partyId) => _parties.TryGetValue(partyId, out var party) ? party.Members.Count : 0;

    // ---- IPartyLootDebts (WP-M4-6) --------------------------------------------------------------------------------

    /// <summary>The loot debts' half-life (<c>PartyLootParams.debtHalfLifeMs</c>); 0 without party loot (debts are not kept).</summary>
    public int LootDebtHalfLifeMs { get; set; }

    public long DebtOf(long partyId, long characterId, long nowMs)
    {
        if (LootDebtHalfLifeMs <= 0 || !_parties.TryGetValue(partyId, out var party)
            || !party.Debts.TryGetValue(characterId, out var debt))
            return 0;
        return Shared.Simulation.Items.PartyLoot.DecayDebt(debt.Gold, nowMs - debt.AtMs, LootDebtHalfLifeMs);
    }

    public void SetDebt(long partyId, long characterId, long debt, long nowMs)
    {
        if (LootDebtHalfLifeMs <= 0 || !_parties.TryGetValue(partyId, out var party) || !party.Members.Contains(characterId))
            return;
        if (debt <= 0)
            party.Debts.Remove(characterId);
        else
            party.Debts[characterId] = new LootDebt(debt, nowMs);
    }

    // ---- requests (phase 1) ---------------------------------------------------------------------------------------

    /// <summary>
    /// Phase 1: one <see cref="PartyRequest"/> of <paramref name="requester"/> at <paramref name="nowMs"/>; the answer goes to its
    /// <see cref="PartyAnswers"/> at once (party changes take effect at once and are announced on the bus).
    /// </summary>
    public void Handle(Player requester, uint requestId, PartyAction action, uint inviteId, uint targetEntityId, string targetName,
        long nowMs)
    {
        var reason = action switch
        {
            PartyAction.Invite => Invite(requester, targetEntityId, targetName, nowMs),
            PartyAction.Accept => Accept(requester, inviteId, nowMs),
            PartyAction.Decline => Decline(requester, inviteId),
            PartyAction.Leave => Leave(requester),
            PartyAction.Kick => Kick(requester, targetEntityId, targetName),
            PartyAction.PassLead => PassLead(requester, targetEntityId, targetName),
            _ => PartyReason.PartyNotFound,
        };
        requester.Party.Results.Add(new PartyResult { RequestId = requestId, Action = action, Reason = reason });
    }

    private PartyReason Invite(Player inviter, uint targetEntityId, string targetName, long nowMs)
    {
        var target = targetEntityId != 0 ? FindVisible(inviter, targetEntityId)
            : string.IsNullOrWhiteSpace(targetName) ? null : FindByName(inviter, targetName.Trim());
        if (target == null)
            return targetEntityId == inviter.Id ? PartyReason.PartySelf : PartyReason.PartyTargetNotFound;
        if (target == inviter)
            return PartyReason.PartySelf;
        long partyId = 0;
        if (_byMember.TryGetValue(inviter.CharacterId, out var party))
        {
            if (party.Leader != inviter.CharacterId)
                return PartyReason.PartyNotLeader;
            if (party.Members.Count >= _settings.MaxMembers)
                return PartyReason.PartyFull;
            partyId = party.Id;
        }
        if (_byMember.ContainsKey(target.CharacterId))
            return PartyReason.PartyAlreadyIn;
        // TODO(open): one pending invite per invitee lets any player hold a target's slot for the whole timeout (PR #169
        // review); whether an invitee may hold several invites, or a newer one replaces the older, is the user's to decide.
        if (FindInvite(target.CharacterId) >= 0)
            return PartyReason.PartyInvitePending;

        var invite = new PendingInvite(++_nextInviteId, inviter.CharacterId, target.CharacterId, partyId, _nextPartyId,
            nowMs + _settings.InviteTimeoutMs);
        _invites.Add(invite);
        _expired.Remove(target.CharacterId);
        target.Party.Invites.Add(new PartyInvite
        {
            InviteId = invite.Id,
            InviterEntityId = inviter.Id,
            InviterName = DisplayIdentity.For(inviter, target),
            TimeoutMs = (uint)_settings.InviteTimeoutMs,
        });
        return PartyReason.None;
    }

    private PartyReason Accept(Player invitee, uint inviteId, long nowMs)
    {
        int index = FindInvite(invitee.CharacterId);
        if (index < 0 || _invites[index].Id != inviteId)
            return WasExpired(invitee.CharacterId, inviteId) ? PartyReason.PartyInviteExpired : PartyReason.PartyNotFound;
        var invite = _invites[index];
        _invites.RemoveAt(index);
        if (nowMs >= invite.ExpiresAtMs)
        {
            Notify(invite.InviterCharacterId, PartyReason.PartyInviteExpired);
            return PartyReason.PartyInviteExpired;
        }
        if (_byMember.ContainsKey(invitee.CharacterId))
        {
            Notify(invite.InviterCharacterId, PartyReason.PartyAlreadyIn); // the inviter hears why nobody joined
            return PartyReason.PartyAlreadyIn;
        }
        if (PlayerOf(invite.InviterCharacterId) == null)
            return PartyReason.PartyNotFound; // the inviter left the world
        _byMember.TryGetValue(invite.InviterCharacterId, out var inviterParty);
        if (!StillGood(invite, inviterParty))
        {
            Notify(invite.InviterCharacterId, PartyReason.PartyNotFound);
            return PartyReason.PartyNotFound; // the inviter's party dissolved, or the inviter left it or joined another
        }
        if (inviterParty == null)
        {
            var party = new Party(++_nextPartyId, invite.InviterCharacterId, _settings.MaxMembers);
            _parties.Add(party.Id, party);
            Join(party, invite.InviterCharacterId, PartyChangeKind.Formed);
            Join(party, invitee.CharacterId, PartyChangeKind.Joined);
            _events?.Publish(new GroupFormed(party.Id, party.Leader, invitee.CharacterId), invitee.CharacterId);
            return PartyReason.None;
        }
        if (inviterParty.Members.Count >= _settings.MaxMembers)
        {
            Notify(invite.InviterCharacterId, PartyReason.PartyFull);
            return PartyReason.PartyFull;
        }
        Join(inviterParty, invitee.CharacterId, PartyChangeKind.Joined);
        return PartyReason.None;
    }

    /// <summary>
    /// Whether the inviter's party now is the one the invite was for: the same party, or, for an invite sent with no party, still
    /// none or a party formed since that the inviter leads (it invited several players before the first accepted). Refused when
    /// the inviter left the party the invite was for, or is now a plain member of another party (PR #169 review).
    /// </summary>
    private static bool StillGood(in PendingInvite invite, Party? inviterParty)
    {
        if (invite.PartyId != 0)
            return inviterParty != null && inviterParty.Id == invite.PartyId;
        return inviterParty == null
            || (inviterParty.Id > invite.LastPartyIdThen && inviterParty.Leader == invite.InviterCharacterId);
    }

    private PartyReason Decline(Player invitee, uint inviteId)
    {
        int index = FindInvite(invitee.CharacterId);
        if (index < 0 || _invites[index].Id != inviteId)
            return WasExpired(invitee.CharacterId, inviteId) ? PartyReason.PartyInviteExpired : PartyReason.PartyNotFound;
        var invite = _invites[index];
        _invites.RemoveAt(index);
        Notify(invite.InviterCharacterId, PartyReason.PartyDeclined);
        return PartyReason.None;
    }

    private PartyReason Leave(Player member)
    {
        if (!_byMember.TryGetValue(member.CharacterId, out var party))
            return PartyReason.PartyNotFound;
        Remove(party, member.CharacterId, PartyChangeKind.Left);
        return PartyReason.None;
    }

    private PartyReason Kick(Player leader, uint targetEntityId, string targetName)
    {
        var reason = LeaderTarget(leader, targetEntityId, targetName, out var party, out long target);
        if (reason != PartyReason.None)
            return reason;
        Remove(party!, target, PartyChangeKind.Kicked);
        return PartyReason.None;
    }

    private PartyReason PassLead(Player leader, uint targetEntityId, string targetName)
    {
        var reason = LeaderTarget(leader, targetEntityId, targetName, out var party, out long target);
        if (reason != PartyReason.None)
            return reason;
        party!.Leader = target;
        Announce(party, target, PartyChangeKind.LeaderChanged);
        return PartyReason.None;
    }

    /// <summary>
    /// The checks of a leader's action on a member, named by its entity (wherever it is: a member keeps its entity while it
    /// lingers) or else by the name the leader sees. Character ids never come from a client (spec 6.2).
    /// </summary>
    private PartyReason LeaderTarget(Player leader, uint targetEntityId, string targetName, out Party? party, out long target)
    {
        target = 0;
        if (!_byMember.TryGetValue(leader.CharacterId, out party))
            return PartyReason.PartyNotFound;
        if (party.Leader != leader.CharacterId)
            return PartyReason.PartyNotLeader;
        if (targetEntityId != 0)
        {
            foreach (long member in party.Members)
            {
                if (PlayerOf(member) is { } player && player.Id == targetEntityId)
                {
                    target = member;
                    break;
                }
            }
        }
        else if (!string.IsNullOrWhiteSpace(targetName))
        {
            string name = targetName.Trim();
            foreach (long member in party.Members)
            {
                if (PlayerOf(member) is { } player && string.Equals(DisplayIdentity.For(player, leader), name, StringComparison.OrdinalIgnoreCase))
                {
                    target = member;
                    break;
                }
            }
        }
        if (target == 0)
            return PartyReason.PartyTargetNotFound;
        return target == leader.CharacterId ? PartyReason.PartySelf : PartyReason.None;
    }

    // ---- time and the world ---------------------------------------------------------------------------------------

    /// <summary>Phase 1, before the requests: invites past their time are dropped and their inviters told.</summary>
    public void Sweep(long nowMs)
    {
        for (int i = 0; i < _invites.Count;)
        {
            if (nowMs < _invites[i].ExpiresAtMs)
            {
                i++;
                continue;
            }
            long inviter = _invites[i].InviterCharacterId;
            _expired[_invites[i].InviteeCharacterId] = _invites[i].Id; // a late accept or decline hears "expired"
            _invites.RemoveAt(i);
            Notify(inviter, PartyReason.PartyInviteExpired);
        }
    }

    /// <summary>
    /// The world removed the character (its linger ended, it logged out, or its ownership was lost; R-2.1-08): it leaves its
    /// party, and the invites to and from it are dropped (an accept of one is refused <see cref="PartyReason.PartyNotFound"/>).
    /// </summary>
    public void Detach(long characterId)
    {
        _invites.RemoveAll(invite => invite.InviteeCharacterId == characterId || invite.InviterCharacterId == characterId);
        _expired.Remove(characterId);
        if (_byMember.TryGetValue(characterId, out var party))
            Remove(party, characterId, PartyChangeKind.Left);
    }

    /// <summary>
    /// A character's client (re)connected: an invite still waiting for it is sent again with the time it has left (the new
    /// connection's answers start empty, so the toast would be lost otherwise). Nothing when its inviter left the world.
    /// </summary>
    public void Resend(Player invitee, long nowMs)
    {
        int index = FindInvite(invitee.CharacterId);
        if (index < 0)
            return;
        var invite = _invites[index];
        if (nowMs >= invite.ExpiresAtMs || PlayerOf(invite.InviterCharacterId) is not { } inviter)
            return;
        invitee.Party.Invites.Add(new PartyInvite
        {
            InviteId = invite.Id,
            InviterEntityId = inviter.Id,
            InviterName = DisplayIdentity.For(inviter, invitee),
            TimeoutMs = (uint)(invite.ExpiresAtMs - nowMs),
        });
    }

    /// <summary>A member connected or disconnected: every member's frame shows it at once (not at the next refresh).</summary>
    public void Refresh(long characterId)
    {
        if (!_byMember.TryGetValue(characterId, out var party))
            return;
        foreach (long member in party.Members)
        {
            if (PlayerOf(member) is { } player)
                player.Party.Touch();
        }
    }

    // ---- changes --------------------------------------------------------------------------------------------------

    private void Join(Party party, long characterId, PartyChangeKind change)
    {
        party.Members.Add(characterId);
        _byMember[characterId] = party;
        Announce(party, characterId, change);
    }

    private void Remove(Party party, long characterId, PartyChangeKind change)
    {
        party.Members.Remove(characterId);
        party.Debts.Remove(characterId); // a member that leaves takes no loot debt along (WP-M4-6)
        _byMember.Remove(characterId);
        if (PlayerOf(characterId) is { } gone)
            gone.Party.Mark(change, gone, gone); // its frame empties
        if (party.Members.Count < 2)
        {
            Disband(party, characterId, change);
            return;
        }
        Announce(party, characterId, change);
        if (party.Leader == characterId)
        {
            party.Leader = party.Members[0]; // the earliest-joined member
            Announce(party, party.Leader, PartyChangeKind.LeaderChanged);
        }
    }

    private void Disband(Party party, long causeCharacterId, PartyChangeKind change)
    {
        _events?.Publish(new PartyChanged(party.Id, causeCharacterId, change, party.Members.ToArray()), causeCharacterId);
        foreach (long member in party.Members)
        {
            _byMember.Remove(member);
            if (PlayerOf(member) is { } player)
                player.Party.Mark(PartyChangeKind.Disbanded, PlayerOf(causeCharacterId), player);
            _events?.Publish(new PartyChanged(party.Id, member, PartyChangeKind.Disbanded, Array.Empty<long>()), member);
        }
        party.Members.Clear();
        party.Debts.Clear();
        _parties.Remove(party.Id);
    }

    /// <summary>Publishes the change and marks every member's frame.</summary>
    private void Announce(Party party, long characterId, PartyChangeKind change)
    {
        _events?.Publish(new PartyChanged(party.Id, characterId, change, party.Members.ToArray()), characterId);
        var cause = PlayerOf(characterId);
        foreach (long member in party.Members)
        {
            if (PlayerOf(member) is { } player)
                player.Party.Mark(change, cause, player);
        }
    }

    private void Notify(long characterId, PartyReason reason)
    {
        if (PlayerOf(characterId) is { } player)
            player.Party.Results.Add(new PartyResult { RequestId = 0, Action = PartyAction.Invite, Reason = reason });
    }

    private bool WasExpired(long inviteeCharacterId, uint inviteId) =>
        inviteId != 0 && _expired.TryGetValue(inviteeCharacterId, out uint expired) && expired == inviteId;

    private int FindInvite(long inviteeCharacterId)
    {
        for (int i = 0; i < _invites.Count; i++)
        {
            if (_invites[i].InviteeCharacterId == inviteeCharacterId)
                return i;
        }
        return -1;
    }

    private sealed class Party
    {
        public Party(long id, long leader, int capacity)
        {
            Id = id;
            Leader = leader;
            Members = new List<long>(capacity);
        }

        public long Id { get; }

        public long Leader { get; set; }

        /// <summary>In join order.</summary>
        public List<long> Members { get; }

        /// <summary>The members' loot debts as last written (WP-M4-6), decayed when read.</summary>
        public Dictionary<long, LootDebt> Debts { get; } = new();
    }

    /// <summary>A loot debt in gold as it stood at <see cref="AtMs"/> (world simulation ms).</summary>
    private readonly record struct LootDebt(long Gold, long AtMs);

    /// <param name="PartyId">The inviter's party when it invited; 0 for none.</param>
    /// <param name="LastPartyIdThen">The last party id formed when the invite was sent: a party with a higher id formed since.</param>
    private readonly record struct PendingInvite(uint Id, long InviterCharacterId, long InviteeCharacterId, long PartyId, long LastPartyIdThen,
        long ExpiresAtMs);
}
