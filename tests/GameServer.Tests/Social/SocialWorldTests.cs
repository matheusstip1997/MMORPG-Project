using GameServer.Entities;
using GameServer.Events;
using GameServer.Persistence;
using GameServer.Sessions;
using GameServer.Tests.Support;
using Shared.Protocol;

namespace GameServer.Tests.Social;

/// <summary>
/// Friends, mute and block in the world (spec 6.5 "M4 social", M4 decision 16; WP-M4-16) through the router, phase 1, the saves
/// and the social sender: a mutual friendship (request, accept, decline, timeout), the caps, double submits, the answer after the
/// commit, the online state across a relog, and what a mute or a block stops (chat lines on every channel, whispers, party
/// invites), decided on the server. Names are the harness's character names (<c>c{account}_{slot}</c>) through
/// <c>DisplayIdentity</c>.
/// </summary>
public class SocialWorldTests
{
    private sealed class Fixture
    {
        public Fixture(Shared.Data.Tables.SocialSettings? social = null, bool party = false)
        {
            Bus = new EventBus(1, Harness0Clock, TestPipelineSettings.BusLimits());
            Bus.Subscribe<SocialChanged>(Log);
            Harness = new WorldHarness(events: new EventDispatchHook(Bus), bus: Bus, chat: TestWorlds.Chat(burst: 50, messagesPerPeriod: 50),
                party: party ? TestWorlds.Party() : null, social: social ?? TestWorlds.Social());
        }

        private FakeClock Harness0Clock { get; } = new();
        public WorldHarness Harness { get; }
        public EventBus Bus { get; }
        public EventLog Log { get; } = new();
        public GameServer.Social.SocialService Social => Harness.World.Social!;
        private uint _requests;
        private readonly HashSet<int> _joined = [];

        public Player Join(int peerId, int xCm = 0)
        {
            if (_joined.Add(peerId))
                Harness.Characters.Add(peerId, 0, new CharacterState(1, 0, SessionManager.MainWorldKey, xCm, 0, 0, 0, 100, 10, 100));
            return Harness.Join(peerId);
        }

        public uint Send(int peerId, SocialAction action, string name = "", uint reference = 0, uint? requestId = null)
        {
            uint id = requestId ?? ++_requests;
            Harness.SendSocial(peerId, id, action, name, reference);
            return id;
        }

        public SocialResult Answer(int peerId, uint requestId) =>
            Assert.Single(Harness.Transport.SocialResultsFor(peerId), r => r.RequestId == requestId);

        public List<SocialResult> Notices(int peerId) => Harness.Transport.SocialResultsFor(peerId).Where(r => r.RequestId == 0).ToList();

        public SocialState State(int peerId) => Harness.Transport.SocialStatesFor(peerId)[^1];

        /// <summary>Peer <paramref name="from"/> asks peer <paramref name="to"/>, who accepts by its incoming ref.</summary>
        public void Befriend(int from, int to)
        {
            var ask = Send(from, SocialAction.AddFriend, $"c{to}_0");
            Harness.Ticks(1);
            Assert.Equal((SocialReason.None, SocialNotice.RequestSent), (Answer(from, ask).Reason, Answer(from, ask).Notice));
            uint reference = Assert.Single(State(to).Incoming).Ref;
            var accept = Send(to, SocialAction.AcceptFriend, reference: reference);
            Harness.Ticks(3);
            Assert.Equal((SocialReason.None, SocialNotice.FriendAdded), (Answer(to, accept).Reason, Answer(to, accept).Notice));
        }
    }

    internal sealed class EventLog : IEventConsumer<SocialChanged>
    {
        public List<SocialChanged> Events { get; } = [];
        public void Handle(in EventEnvelope envelope, in SocialChanged payload) => Events.Add(payload);
    }

    [Fact]
    public void A_request_and_its_accept_make_both_players_friends_with_their_online_state()
    {
        var f = new Fixture();
        var a = f.Join(1);
        var b = f.Join(2);
        f.Harness.Ticks(1);
        Assert.Empty(f.State(1).Friends); // the empty lists come with the enter world

        var ask = f.Send(1, SocialAction.AddFriend, "C2_0"); // display names ignore case
        f.Harness.Ticks(1);
        Assert.Equal(("c2_0", SocialNotice.RequestSent), (f.Answer(1, ask).Name, f.Answer(1, ask).Notice));
        var notice = Assert.Single(f.Notices(2));
        Assert.Equal((SocialNotice.RequestReceived, "c1_0"), (notice.Notice, notice.Name));
        var incoming = Assert.Single(f.State(2).Incoming);
        Assert.Equal("c1_0", incoming.Name);
        Assert.Empty(f.State(1).Friends); // a request is no friendship

        var accept = f.Send(2, SocialAction.AcceptFriend, reference: incoming.Ref);
        f.Harness.Ticks(3);

        Assert.Equal((SocialReason.None, SocialNotice.FriendAdded, "c1_0"),
            (f.Answer(2, accept).Reason, f.Answer(2, accept).Notice, f.Answer(2, accept).Name));
        Assert.Contains(f.Notices(1), n => n.Notice == SocialNotice.FriendAdded && n.Name == "c2_0");
        var friendOfA = Assert.Single(f.State(1).Friends);
        Assert.Equal(("c2_0", true), (friendOfA.Name, friendOfA.Online));
        var friendOfB = Assert.Single(f.State(2).Friends);
        Assert.Equal(("c1_0", true), (friendOfB.Name, friendOfB.Online));
        Assert.Empty(f.State(2).Incoming);
        // One pair row in the accepter's save, both names as each side saw the other.
        var row = Assert.Single(f.Harness.Characters.Friends);
        Assert.Equal((Math.Min(a.CharacterId, b.CharacterId), Math.Max(a.CharacterId, b.CharacterId)), row.Key);
        var added = Assert.Single(f.Log.Events);
        Assert.Equal((b.CharacterId, a.CharacterId, SocialOpKind.FriendAdd), (added.CharacterId, added.OtherId, added.Op));
    }

    [Fact]
    public void An_add_back_accepts_the_other_players_request()
    {
        var f = new Fixture();
        f.Join(1);
        f.Join(2);
        f.Send(1, SocialAction.AddFriend, "c2_0");
        f.Harness.Ticks(1);

        var back = f.Send(2, SocialAction.AddFriend, "c1_0");
        f.Harness.Ticks(3);

        Assert.Equal(SocialNotice.FriendAdded, f.Answer(2, back).Notice);
        Assert.Single(f.State(1).Friends);
        Assert.Single(f.Harness.Characters.Friends);
    }

    [Fact]
    public void A_friend_shows_offline_while_away_and_both_lists_come_back_after_a_relog()
    {
        var f = new Fixture();
        f.Join(1);
        f.Join(2);
        f.Befriend(1, 2);

        f.Harness.Leave(2); // no linger in the harness: the character leaves the world after its last save
        f.Harness.Ticks(3);
        Assert.False(Assert.Single(f.State(1).Friends).Online);

        f.Join(2);
        f.Harness.Ticks(1);
        var back = Assert.Single(f.State(2).Friends); // loaded at login from the saved row
        Assert.Equal(("c1_0", true), (back.Name, back.Online));
        Assert.True(Assert.Single(f.State(1).Friends).Online);
    }

    [Fact]
    public void A_decline_tells_the_asker_and_a_request_expires_in_time()
    {
        var f = new Fixture(TestWorlds.Social(requestTimeoutMs: 1_000));
        f.Join(1);
        f.Join(2);
        f.Join(3);
        f.Send(1, SocialAction.AddFriend, "c2_0");
        f.Send(3, SocialAction.AddFriend, "c2_0");
        f.Harness.Ticks(1);
        var requests = f.State(2).Incoming;
        Assert.Equal(["c1_0", "c3_0"], requests.Select(r => r.Name));

        var decline = f.Send(2, SocialAction.DeclineFriend, reference: requests[0].Ref);
        f.Harness.Ticks(1);
        Assert.Equal(SocialReason.None, f.Answer(2, decline).Reason);
        Assert.Contains(f.Notices(1), n => n.Notice == SocialNotice.RequestDeclined && n.Name == "c2_0");
        Assert.Single(f.State(2).Incoming);

        f.Harness.Clock.Advance(1_000);
        f.Harness.Ticks(1);
        Assert.Contains(f.Notices(3), n => n.Notice == SocialNotice.RequestExpired && n.Name == "c2_0");
        Assert.Empty(f.State(2).Incoming);
        var late = f.Send(2, SocialAction.AcceptFriend, reference: requests[1].Ref);
        f.Harness.Ticks(1);
        Assert.Equal(SocialReason.SocialEntryNotFound, f.Answer(2, late).Reason);
        Assert.Empty(f.Harness.Characters.Friends);
    }

    [Theory]
    [InlineData("nobody", SocialReason.SocialTargetNotFound)]
    [InlineData("c1_0", SocialReason.SocialSelf)]
    [InlineData("", SocialReason.SocialTargetNotFound)]
    public void An_add_names_another_online_player(string name, SocialReason reason)
    {
        var f = new Fixture();
        f.Join(1);
        var ask = f.Send(1, SocialAction.AddFriend, name);
        f.Harness.Ticks(1);
        Assert.Equal(reason, f.Answer(1, ask).Reason);
    }

    [Fact]
    public void The_friend_cap_holds_on_both_sides()
    {
        var f = new Fixture(TestWorlds.Social(maxFriends: 1));
        f.Join(1);
        f.Join(2);
        f.Join(3);
        f.Befriend(1, 2);

        var toFull = f.Send(3, SocialAction.AddFriend, "c1_0");
        var fromFull = f.Send(1, SocialAction.AddFriend, "c3_0");
        f.Harness.Ticks(1);

        Assert.Equal(SocialReason.SocialTargetFriendCap, f.Answer(3, toFull).Reason);
        Assert.Equal(SocialReason.SocialFriendCap, f.Answer(1, fromFull).Reason);
    }

    [Fact]
    public void A_change_is_answered_and_applied_only_once_its_save_committed()
    {
        var f = new Fixture();
        f.Join(1);
        f.Join(2);
        f.Harness.Ticks(1);
        f.Harness.Saves.AutoComplete = false;

        var mute = f.Send(1, SocialAction.Mute, "c2_0");
        f.Harness.Ticks(3);
        Assert.Empty(f.Harness.Transport.SocialResultsFor(1));
        Assert.False(f.Social.HidesChatFrom(1_000_000, 0)); // nothing for anyone yet
        Assert.False(f.Harness.World.SocialQuery.HidesChatFrom(f.Harness.World.TryGetPlayer(1, out var a) ? a.CharacterId : 0,
            f.Harness.World.TryGetPlayer(2, out var b) ? b.CharacterId : 0));

        f.Harness.Saves.CompleteAll();
        f.Harness.Ticks(1);

        Assert.Equal(SocialReason.None, f.Answer(1, mute).Reason);
        Assert.True(f.Social.HidesChatFrom(a.CharacterId, b.CharacterId));
        Assert.False(f.Social.Blocks(a.CharacterId, b.CharacterId)); // a mute refuses nothing
        var entry = Assert.Single(f.State(1).Blocks);
        Assert.Equal(("c2_0", SocialBlockKind.Mute), (entry.Name, entry.Kind));
    }

    [Fact]
    public void A_repeated_request_id_changes_once_and_gets_the_first_answer()
    {
        var f = new Fixture();
        f.Join(1);
        f.Join(2);
        f.Harness.Ticks(1);

        f.Send(1, SocialAction.Block, "c2_0", requestId: 7);
        f.Send(1, SocialAction.Block, "c2_0", requestId: 7); // a double submit in the same tick
        f.Harness.Ticks(3);
        f.Send(1, SocialAction.Block, "c2_0", requestId: 7); // and again once answered
        f.Harness.Ticks(1);

        var answers = f.Harness.Transport.SocialResultsFor(1).Where(r => r.RequestId == 7).ToList();
        Assert.Equal(2, answers.Count);
        Assert.All(answers, r => Assert.Equal(SocialReason.None, r.Reason));
        Assert.Single(f.Harness.Saves.Jobs.Where(j => j.Social != null).SelectMany(j => j.Social!.Ops));
    }

    [Fact]
    public void A_muted_or_blocked_players_lines_never_reach_the_player_who_muted_or_blocked_them()
    {
        var f = new Fixture();
        var a = f.Join(1);
        f.Join(2);
        f.Join(3);
        f.Harness.Ticks(1);
        f.Send(2, SocialAction.Mute, "c1_0");
        f.Harness.Ticks(3);

        f.Harness.SendChat(1, "hello");
        f.Harness.SendChat(1, "psst", ChatChannel.Whisper, "c2_0"); // a whisper to a player who muted the sender: dropped silently
        f.Harness.Ticks(1);

        Assert.DoesNotContain(f.Harness.Transport.ChatMessagesFor(2), l => l.SenderEntityId == a.Id);
        Assert.Contains(f.Harness.Transport.ChatMessagesFor(3), l => l.Text == "hello");
        Assert.Equal(2, f.Harness.Transport.ChatMessagesFor(1).Count(l => l.SenderEntityId == a.Id)); // both echoes
        Assert.Empty(f.Harness.Transport.ChatRefusalsFor(1)); // a mute is never revealed

        // The muted player still hears the one who muted it.
        f.Harness.SendChat(2, "still here");
        f.Harness.Ticks(1);
        Assert.Contains(f.Harness.Transport.ChatMessagesFor(1), l => l.Text == "still here");
    }

    [Fact]
    public void A_block_refuses_whispers_both_ways_and_party_invites_and_ends_a_friendship()
    {
        var f = new Fixture(party: true);
        var a = f.Join(1);
        var b = f.Join(2);
        f.Befriend(1, 2);

        var block = f.Send(2, SocialAction.Block, "c1_0");
        f.Harness.Ticks(3);
        Assert.Equal(SocialReason.None, f.Answer(2, block).Reason);
        Assert.Empty(f.State(1).Friends);
        Assert.Empty(f.State(2).Friends);
        Assert.Empty(f.Harness.Characters.Friends);
        Assert.True(f.Social.Blocks(b.CharacterId, a.CharacterId));

        f.Harness.SendChat(1, "hi", ChatChannel.Whisper, "c2_0");
        f.Harness.SendChat(2, "hi", ChatChannel.Whisper, "c1_0");
        f.Harness.SendParty(1, 50, PartyAction.Invite, targetName: "c2_0");
        var again = f.Send(1, SocialAction.AddFriend, "c2_0");
        f.Harness.Ticks(1);

        Assert.Equal([ChatReason.ChatTargetNotFound], f.Harness.Transport.ChatRefusalsFor(1)); // the block looks like no such player
        Assert.Equal([ChatReason.ChatTargetBlocked], f.Harness.Transport.ChatRefusalsFor(2));
        Assert.Equal(PartyReason.PartyTargetNotFound, Assert.Single(f.Harness.Transport.PartyResultsFor(1), r => r.RequestId == 50).Reason);
        Assert.Equal(SocialReason.SocialTargetNotFound, f.Answer(1, again).Reason);
        Assert.Empty(f.Harness.Transport.PartyInvitesFor(2));

        var unblock = f.Send(2, SocialAction.Unblock, "C1_0");
        f.Harness.Ticks(3);
        Assert.Equal(SocialReason.None, f.Answer(2, unblock).Reason);
        Assert.Empty(f.State(2).Blocks);
        Assert.False(f.Social.Blocks(b.CharacterId, a.CharacterId));
    }

    [Fact]
    public void Blocking_ends_a_waiting_friend_request_and_the_cap_counts_mutes_and_blocks()
    {
        var f = new Fixture(TestWorlds.Social(maxBlocks: 1));
        f.Join(1);
        f.Join(2);
        f.Join(3);
        f.Send(1, SocialAction.AddFriend, "c2_0");
        f.Harness.Ticks(1);
        Assert.Single(f.State(2).Incoming);

        f.Send(2, SocialAction.Block, "c1_0");
        f.Harness.Ticks(3);
        Assert.Empty(f.State(2).Incoming);
        var full = f.Send(2, SocialAction.Mute, "c3_0");
        var change = f.Send(2, SocialAction.Mute, "c1_0"); // a block made a mute takes no new room
        f.Harness.Ticks(3);

        Assert.Equal(SocialReason.SocialBlockCap, f.Answer(2, full).Reason);
        Assert.Equal(SocialReason.None, f.Answer(2, change).Reason);
        Assert.Equal(SocialBlockKind.Mute, Assert.Single(f.State(2).Blocks).Kind);
    }

    [Fact]
    public void Social_requests_have_a_rate_limit()
    {
        var f = new Fixture(TestWorlds.Social(requestsPerPeriod: 1, burst: 1));
        f.Join(1);
        f.Join(2);
        var first = f.Send(1, SocialAction.Mute, "c2_0");
        var second = f.Send(1, SocialAction.Unblock, "c2_0");
        f.Harness.Ticks(3);

        Assert.Equal(SocialReason.SocialRateLimited, f.Answer(1, second).Reason);
        Assert.Equal(SocialReason.None, f.Answer(1, first).Reason);
        Assert.Equal(1, f.Harness.World.SocialRateLimited);
    }
}
