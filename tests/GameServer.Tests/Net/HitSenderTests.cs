using GameServer.Net;
using GameServer.Tests.Support;
using Shared.Protocol;

namespace GameServer.Tests.Net;

/// <summary>Phase 10 hit messages (backlog D3): only targets the observer has in interest, split by the message cap.</summary>
public class HitSenderTests
{
    private static HitEntry Hit(uint target) => new() { AttackerId = 1, TargetId = target, Amount = 5, Kind = HitKind.Damaged };

    [Fact]
    public void An_observer_gets_the_hits_on_entities_in_its_interest_and_on_those_that_just_left()
    {
        var f = new HitFixture();
        var observer = f.Player(1);
        var interest = observer.Interest;
        interest.VisibleList.AddRange([observer, f.Monster(2), f.Monster(5), f.Monster(9)]); // sorted by id, as interest keeps it
        interest.Left.Add(7);
        var transport = new FakeTransport();

        new HitSender(transport, 1_200).Send(observer, 40, [Hit(5), Hit(8), Hit(7), Hit(3), Hit(9), Hit(1)]);

        Assert.Equal([5u, 7u, 9u, 1u], transport.HitsFor(observer.PeerId).Select(h => h.Hit.TargetId));
        Assert.All(transport.HitsFor(observer.PeerId), h => Assert.Equal(40u, h.Tick));
        Assert.Single(transport.Sent);
    }

    [Fact]
    public void Nothing_in_interest_sends_nothing()
    {
        var f = new HitFixture();
        var observer = f.Player(1);
        var transport = new FakeTransport();

        new HitSender(transport, 1_200).Send(observer, 40, [Hit(5)]);

        Assert.Empty(transport.Sent);
    }

    [Fact]
    public void Hits_beyond_one_message_go_out_in_further_messages()
    {
        var f = new HitFixture();
        var observer = f.Player(1);
        observer.Interest.VisibleList.Add(f.Monster(2));
        var transport = new FakeTransport();
        var sender = new HitSender(transport, HitMessage.SizeFor(2));

        sender.Send(observer, 40, [Hit(2), Hit(2), Hit(2), Hit(2), Hit(2)]);

        Assert.Equal(3, transport.Sent.Count);
        Assert.Equal(3, sender.MessagesSent);
        Assert.Equal(5, transport.HitsFor(observer.PeerId).Count);
        Assert.All(transport.Sent, s => Assert.Equal(Channel.UnreliableSequenced, s.Channel));
    }

    [Fact]
    public void Messages_stay_within_the_peer_s_current_limit_when_it_is_below_the_cap()
    {
        var f = new HitFixture();
        var observer = f.Player(1);
        observer.Interest.VisibleList.Add(f.Monster(2));
        var transport = new FakeTransport { PeerMaxUnreliableBytes = HitMessage.SizeFor(2) };

        new HitSender(transport, 1_200).Send(observer, 40, [Hit(2), Hit(2), Hit(2), Hit(2), Hit(2)]);

        Assert.Equal(3, transport.Sent.Count);
        Assert.Equal(5, transport.HitsFor(observer.PeerId).Count);
        Assert.Equal(0, transport.UnreliableTooLarge);
    }

    [Fact]
    public void A_peer_that_is_gone_gets_nothing()
    {
        var f = new HitFixture();
        var observer = f.Player(1);
        observer.Interest.VisibleList.Add(f.Monster(2));
        var transport = new FakeTransport { PeerMaxUnreliableBytes = 0 };

        new HitSender(transport, 1_200).Send(observer, 40, [Hit(2)]);

        Assert.Empty(transport.Sent);
    }

    [Fact]
    public void A_cap_too_small_for_one_hit_is_refused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new HitSender(new FakeTransport(), HitMessage.SizeFor(1) - 1));
}
