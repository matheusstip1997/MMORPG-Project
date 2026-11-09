using GameServer.Entities;
using GameServer.Events;
using GameServer.Persistence;
using GameServer.Sessions;
using GameServer.Tests.Support;
using GameServer.World;
using Shared.Protocol;

namespace GameServer.Tests.Social;

/// <summary>
/// Parties in the world (spec 6.5 "M4 party", M4 decision 1; WP-M4-2) through the router, phase 1 and the party sender:
/// invite, accept, decline, leave, kick, pass the lead, the invite timeout, the races of one tick (players in entity-id
/// order, rule 6), dissolving below two, and a member leaving when its character leaves the world (R-2.1-08). Names are the
/// harness's character names (<c>c{account}_{slot}</c>) through <c>DisplayIdentity</c>.
/// </summary>
public class PartyWorldTests
{
    private sealed class Fixture
    {
        public Fixture(int maxMembers = 6, int inviteTimeoutMs = 60_000, int lingerMs = 0, int stateIntervalMs = 1_000)
        {
            Bus = new EventBus(1, Clock, TestPipelineSettings.BusLimits());
            Bus.Subscribe<PartyChanged>(Log);
            Bus.Subscribe<GroupFormed>(Log);
            Harness = new WorldHarness(events: new EventDispatchHook(Bus), bus: Bus,
                values: TestSessions.Values(lingerOutOfCombatMs: lingerMs),
                party: TestWorlds.Party(maxMembers: maxMembers, inviteTimeoutMs: inviteTimeoutMs, stateIntervalMs: stateIntervalMs));
        }

        private FakeClock Clock { get; } = new();
        public WorldHarness Harness { get; }
        public EventBus Bus { get; }
        public EventLog Log { get; } = new();
        public GameServer.Social.PartyService Parties => Harness.World.Parties!;
        private uint _requests;

        public Player Join(int peerId, int xCm = 0)
        {
            Harness.Characters.Add(peerId, 0, new CharacterState(1, 0, SessionManager.MainWorldKey, xCm, 0, 0, 0, 100, 10, 100));
            return Harness.Join(peerId);
        }

        public uint Send(int peerId, PartyAction action, uint inviteId = 0, string name = "", uint entity = 0)
        {
            uint id = ++_requests;
            Harness.SendParty(peerId, id, action, inviteId, entity, name);
            return id;
        }

        public PartyReason Answer(int peerId, uint requestId) =>
            Assert.Single(Harness.Transport.PartyResultsFor(peerId), r => r.RequestId == requestId).Reason;

        /// <summary>Invites peer <paramref name="to"/> by name from <paramref name="from"/>, ticks, and returns the invite id.</summary>
        public uint Invite(int from, int to)
        {
            var request = Send(from, PartyAction.Invite, name: $"c{to}_0");
            Harness.Ticks(1);
            Assert.Equal(PartyReason.None, Answer(from, request));
            return Harness.Transport.PartyInvitesFor(to)[^1].InviteId;
        }

        /// <summary>A party of the peers in order, the first the leader.</summary>
        public void Form(params int[] peers)
        {
            for (int i = 1; i < peers.Length; i++)
            {
                uint invite = Invite(peers[0], peers[i]);
                var accept = Send(peers[i], PartyAction.Accept, invite);
                Harness.Ticks(1);
                Assert.Equal(PartyReason.None, Answer(peers[i], accept));
            }
        }

        public PartyState LastState(int peerId) => Harness.Transport.PartyStatesFor(peerId)[^1];

        public long[] Members(Player player)
        {
            var into = new long[8];
            int count = Parties.MembersOf(Parties.PartyIdOf(player.CharacterId), into);
            return into[..count];
        }
    }

    internal sealed class EventLog : IEventConsumer<PartyChanged>, IEventConsumer<GroupFormed>
    {
        public List<object> Events { get; } = [];
        public void Handle(in EventEnvelope envelope, in PartyChanged payload) => Events.Add(payload);
        public void Handle(in EventEnvelope envelope, in GroupFormed payload) => Events.Add(payload);
    }

    [Fact]
    public void An_invite_and_its_accept_form_a_party_led_by_the_inviter()
    {
        var f = new Fixture();
        var a = f.Join(1);
        var b = f.Join(2);
        f.Harness.Ticks(1);

        uint invite = f.Invite(1, 2);
        var sent = Assert.Single(f.Harness.Transport.PartyInvitesFor(2));
        Assert.Equal(("c1_0", a.Id, 60_000u), (sent.InviterName, sent.InviterEntityId, sent.TimeoutMs));
        Assert.Equal(0, f.Parties.PartyIdOf(a.CharacterId)); // an invite alone is no party

        var accept = f.Send(2, PartyAction.Accept, invite);
        f.Harness.Ticks(2);

        Assert.Equal(PartyReason.None, f.Answer(2, accept));
        long party = f.Parties.PartyIdOf(a.CharacterId);
        Assert.NotEqual(0, party);
        Assert.True(f.Parties.SameParty(a.CharacterId, b.CharacterId));
        Assert.False(f.Parties.SameParty(a.CharacterId, a.CharacterId));
        Assert.Equal(a.CharacterId, f.Parties.LeaderOf(party));
        Assert.Equal([a.CharacterId, b.CharacterId], f.Members(a));
        foreach (int peer in new[] { 1, 2 })
        {
            var state = f.LastState(peer);
            Assert.Equal((party, a.Id), (state.PartyId, state.LeaderEntityId));
            Assert.Equal(["c1_0", "c2_0"], state.Members.Select(m => m.Name));
            Assert.All(state.Members, m => Assert.True(m.Online));
            Assert.All(state.Members, m => Assert.Equal(0, m.HpPercent)); // no combat rules in this world: no combatant, no HP
        }
        Assert.Equal([
            Changed(party, a.CharacterId, PartyChangeKind.Formed, a.CharacterId),
            Changed(party, b.CharacterId, PartyChangeKind.Joined, a.CharacterId, b.CharacterId),
            (object)new GroupFormed(party, a.CharacterId, b.CharacterId),
        ], f.Log.Events.Select(Normalize));
    }

    [Fact]
    public void Two_invites_to_one_player_in_one_tick_keep_the_first_by_entity_id()
    {
        var f = new Fixture();
        f.Join(1);
        f.Join(2);
        f.Join(3);
        f.Harness.Ticks(1);

        var second = f.Send(2, PartyAction.Invite, name: "c3_0");
        var first = f.Send(1, PartyAction.Invite, name: "c3_0");
        f.Harness.Ticks(1);

        Assert.Equal(PartyReason.None, f.Answer(1, first));
        Assert.Equal(PartyReason.PartyInvitePending, f.Answer(2, second));
        Assert.Equal("c1_0", Assert.Single(f.Harness.Transport.PartyInvitesFor(3)).InviterName);
    }

    [Fact]
    public void An_invite_to_a_full_party_and_an_accept_into_a_party_that_filled_are_refused()
    {
        var f = new Fixture(maxMembers: 3);
        f.Join(1);
        f.Join(2);
        f.Join(3);
        f.Join(4);
        f.Harness.Ticks(1);
        f.Form(1, 2);
        uint toThree = f.Invite(1, 3);
        uint toFour = f.Invite(1, 4);

        var three = f.Send(3, PartyAction.Accept, toThree);
        var four = f.Send(4, PartyAction.Accept, toFour);
        f.Harness.Ticks(1);
        var five = f.Send(1, PartyAction.Invite, name: "c4_0");
        f.Harness.Ticks(1);

        Assert.Equal(PartyReason.None, f.Answer(3, three));
        Assert.Equal(PartyReason.PartyFull, f.Answer(4, four));
        Assert.Equal(PartyReason.PartyFull, f.Answer(1, five));
        Assert.True(f.Harness.World.TryGetPlayer(1, out var leader));
        Assert.Equal(3, f.Parties.MemberCount(f.Parties.PartyIdOf(leader.CharacterId)));
    }

    [Fact]
    public void Two_invites_sent_before_the_party_formed_are_both_accepted_in_turn()
    {
        // PR #169 review: the second accept came after the first formed the party, so its invite (party id 0) was refused.
        var f = new Fixture();
        var a = f.Join(1);
        var b = f.Join(2);
        var c = f.Join(3);
        f.Harness.Ticks(1);
        uint toB = f.Invite(1, 2);
        uint toC = f.Invite(1, 3);

        var acceptB = f.Send(2, PartyAction.Accept, toB);
        f.Harness.Ticks(1);
        var acceptC = f.Send(3, PartyAction.Accept, toC);
        f.Harness.Ticks(2);

        Assert.Equal((PartyReason.None, PartyReason.None), (f.Answer(2, acceptB), f.Answer(3, acceptC)));
        Assert.Equal([a.CharacterId, b.CharacterId, c.CharacterId], f.Members(a));
        Assert.Equal(a.CharacterId, f.Parties.LeaderOf(f.Parties.PartyIdOf(a.CharacterId)));
        Assert.Equal(3, f.LastState(3).Members.Length);
    }

    [Fact]
    public void An_invite_is_refused_once_the_inviter_is_a_plain_member_of_another_party_or_left_the_one_it_was_for()
    {
        var f = new Fixture();
        f.Join(1);
        var b = f.Join(2);
        f.Join(3);
        f.Join(4);
        f.Harness.Ticks(1);
        uint stale = f.Invite(1, 3); // 1 has no party yet
        f.Form(2, 1); // 1 joins 2's party as a plain member

        var accept = f.Send(3, PartyAction.Accept, stale);
        f.Harness.Ticks(1);
        Assert.Equal(PartyReason.PartyNotFound, f.Answer(3, accept));
        Assert.Equal(2, f.Parties.MemberCount(f.Parties.PartyIdOf(b.CharacterId)));

        uint forParty = f.Invite(2, 4); // sent for 2's party
        f.Send(2, PartyAction.Leave); // 2 leaves: the party dissolves
        f.Harness.Ticks(1);
        var late = f.Send(4, PartyAction.Accept, forParty);
        f.Harness.Ticks(1);
        Assert.Equal(PartyReason.PartyNotFound, f.Answer(4, late));
        Assert.Equal(0, f.Parties.PartyCount);
    }

    [Fact]
    public void An_accept_after_the_timeout_is_expired_and_the_inviter_hears_it()
    {
        var f = new Fixture(inviteTimeoutMs: 5_000);
        f.Join(1);
        f.Join(2);
        f.Harness.Ticks(1);
        uint invite = f.Invite(1, 2);

        f.Harness.Clock.Advance(5_000);
        f.Harness.Ticks(1);
        var late = f.Send(2, PartyAction.Accept, invite);
        f.Harness.Ticks(1);

        Assert.Equal(PartyReason.PartyInviteExpired, f.Answer(2, late));
        Assert.Contains(f.Harness.Transport.PartyResultsFor(1), r => r.RequestId == 0 && r.Reason == PartyReason.PartyInviteExpired);
        Assert.Equal(0, f.Parties.PartyCount);
        Assert.Equal(0, f.Parties.PendingInvites);
    }

    [Fact]
    public void A_decline_tells_the_inviter_and_an_accept_of_another_invite_is_not_found()
    {
        var f = new Fixture();
        f.Join(1);
        f.Join(2);
        f.Harness.Ticks(1);
        uint invite = f.Invite(1, 2);

        var wrong = f.Send(2, PartyAction.Accept, invite + 7);
        var decline = f.Send(2, PartyAction.Decline, invite);
        f.Harness.Ticks(1);

        Assert.Equal(PartyReason.PartyNotFound, f.Answer(2, wrong));
        Assert.Equal(PartyReason.None, f.Answer(2, decline));
        Assert.Contains(f.Harness.Transport.PartyResultsFor(1), r => r.RequestId == 0 && r.Reason == PartyReason.PartyDeclined);
        Assert.Equal(0, f.Parties.PartyCount);
    }

    [Fact]
    public void A_leave_and_a_kick_of_one_member_in_one_tick_resolve_by_entity_id()
    {
        var f = new Fixture();
        var a = f.Join(1);
        f.Join(2);
        var c = f.Join(3);
        f.Harness.Ticks(1);
        f.Form(1, 2, 3);

        var leave = f.Send(2, PartyAction.Leave);
        var kick = f.Send(1, PartyAction.Kick, name: "c2_0");
        f.Harness.Ticks(1);

        Assert.Equal(PartyReason.None, f.Answer(1, kick)); // the leader's entity comes first
        Assert.Equal(PartyReason.PartyNotFound, f.Answer(2, leave));
        Assert.Equal([a.CharacterId, c.CharacterId], f.Members(a));
        Assert.Equal(0, f.LastState(2).PartyId);
        Assert.Equal((PartyChangeKind.Kicked, "c2_0"), (f.LastState(1).Cause, f.LastState(1).CauseName));
    }

    [Fact]
    public void Only_the_leader_kicks_and_passes_the_lead_and_never_on_itself()
    {
        var f = new Fixture();
        var a = f.Join(1);
        var b = f.Join(2);
        f.Join(3);
        f.Harness.Ticks(1);
        f.Form(1, 2, 3);

        var memberKick = f.Send(2, PartyAction.Kick, name: "c3_0");
        var selfKick = f.Send(1, PartyAction.Kick, entity: a.Id);
        var stranger = f.Send(1, PartyAction.PassLead, name: "nobody");
        f.Harness.Ticks(1);
        var pass = f.Send(1, PartyAction.PassLead, entity: b.Id);
        f.Harness.Ticks(1);

        Assert.Equal(PartyReason.PartyNotLeader, f.Answer(2, memberKick));
        Assert.Equal(PartyReason.PartySelf, f.Answer(1, selfKick));
        Assert.Equal(PartyReason.PartyTargetNotFound, f.Answer(1, stranger));
        Assert.Equal(PartyReason.None, f.Answer(1, pass));
        Assert.Equal(b.CharacterId, f.Parties.LeaderOf(f.Parties.PartyIdOf(a.CharacterId)));
        Assert.Equal(b.Id, f.LastState(3).LeaderEntityId);
    }

    [Fact]
    public void A_leaving_leader_passes_the_lead_to_the_earliest_member_and_two_leaving_dissolve_the_party()
    {
        var f = new Fixture();
        var a = f.Join(1);
        var b = f.Join(2);
        var c = f.Join(3);
        f.Harness.Ticks(1);
        f.Form(1, 2, 3);
        long party = f.Parties.PartyIdOf(a.CharacterId);

        f.Send(1, PartyAction.Leave);
        f.Harness.Ticks(1);
        Assert.Equal(b.CharacterId, f.Parties.LeaderOf(party));
        Assert.Equal([b.CharacterId, c.CharacterId], f.Members(b));

        f.Send(3, PartyAction.Leave);
        f.Harness.Ticks(2);

        Assert.Equal(0, f.Parties.PartyIdOf(b.CharacterId));
        Assert.Equal(0, f.Parties.PartyCount);
        Assert.Equal((0L, PartyChangeKind.Disbanded), (f.LastState(2).PartyId, f.LastState(2).Cause));
        Assert.Empty(f.LastState(2).Members);
        Assert.Contains(Changed(party, b.CharacterId, PartyChangeKind.Disbanded), f.Log.Events.Select(Normalize));
    }

    [Fact]
    public void A_disconnected_member_stays_offline_while_it_lingers_then_leaves()
    {
        var f = new Fixture(lingerMs: 10_000);
        var a = f.Join(1);
        var b = f.Join(2);
        f.Harness.Ticks(1);
        f.Form(1, 2);

        f.Harness.Leave(2);
        f.Harness.Ticks(2);
        Assert.True(f.Parties.SameParty(a.CharacterId, b.CharacterId));
        Assert.False(Assert.Single(f.LastState(1).Members, m => m.EntityId == b.Id).Online);

        f.Harness.Clock.Advance(10_000);
        f.Harness.Ticks(4);

        Assert.Equal(0, f.Parties.PartyIdOf(a.CharacterId));
        Assert.Equal(0, f.LastState(1).PartyId);
    }

    [Fact]
    public void An_invite_by_entity_needs_the_target_in_interest()
    {
        var f = new Fixture();
        f.Join(1, xCm: 0);
        var far = f.Join(2, xCm: 9_000);
        var near = f.Join(3, xCm: 1_000);
        f.Harness.Ticks(2);

        var hidden = f.Send(1, PartyAction.Invite, entity: far.Id);
        var seen = f.Send(1, PartyAction.Invite, entity: near.Id);
        f.Harness.Ticks(1);

        Assert.Equal(PartyReason.PartyTargetNotFound, f.Answer(1, hidden)); // rule 9: never an entity the inviter cannot see
        Assert.Equal(PartyReason.None, f.Answer(1, seen));
    }

    [Fact]
    public void The_frame_refreshes_hp_at_the_interval_and_a_member_out_of_interest_is_still_listed()
    {
        var f = new Fixture(stateIntervalMs: 1_000);
        f.Join(1, xCm: 0);
        var b = f.Join(2, xCm: 9_000);
        f.Harness.Ticks(1);
        f.Form(1, 2);
        int sent = f.Harness.Transport.PartyStatesFor(1).Count;

        f.Harness.Ticks(3);
        Assert.Equal(sent, f.Harness.Transport.PartyStatesFor(1).Count); // nothing changed, the interval not reached

        f.Harness.Clock.Advance(1_000);
        f.Harness.Ticks(1);
        var refresh = f.LastState(1);
        Assert.Equal(sent + 1, f.Harness.Transport.PartyStatesFor(1).Count);
        Assert.Equal(PartyChangeKind.None, refresh.Cause);
        var member = Assert.Single(refresh.Members, m => m.EntityId == b.Id);
        Assert.Equal((9_000, (ushort)1), (member.XCm, member.Level)); // HP needs combat rules: PartyExpWorldTests
    }

    /// <summary>A <see cref="PartyChanged"/> as a comparable value (its member array compares by reference).</summary>
    private static object Changed(long party, long character, PartyChangeKind change, params long[] members) =>
        (party, character, change, string.Join(",", members));

    private static object Normalize(object e) => e is PartyChanged changed
        ? Changed(changed.PartyId, changed.CharacterId, changed.Change, changed.Members) : e;
}
