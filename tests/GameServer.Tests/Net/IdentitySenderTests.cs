using GameServer.Entities;
using GameServer.Social;
using GameServer.Tests.Support;
using GameServer.World;

namespace GameServer.Tests.Net;

/// <summary>
/// Player names for the name plates (spec 9.3, protocol 9): sent through DisplayIdentity (rule 8) when a player enters an
/// observer's interest, never for a player the observer cannot see (rule 9).
/// </summary>
public class IdentitySenderTests
{
    [Fact]
    public void A_joining_player_gets_its_own_name_once()
    {
        var harness = new WorldHarness();
        var player = harness.Join(1);

        harness.Ticks(5);

        Assert.Equal([(player.Id, "c1_0")], harness.Transport.IdentitiesFor(1));
    }

    [Fact]
    public void Each_observer_gets_the_name_of_a_player_entering_its_interest()
    {
        var harness = new WorldHarness();
        var first = harness.Join(1);
        var second = harness.Join(2);
        harness.Ticks(3);

        Assert.Equal([(first.Id, "c1_0"), (second.Id, "c2_0")], harness.Transport.IdentitiesFor(1));
        Assert.Equal([(first.Id, "c1_0"), (second.Id, "c2_0")], harness.Transport.IdentitiesFor(2).OrderBy(i => i.EntityId));
    }

    [Fact]
    public void A_player_seen_again_after_leaving_interest_is_named_again_and_a_hidden_one_never()
    {
        var visibility = new ToggleVisibility();
        var harness = new WorldHarness(visibility: visibility);
        harness.Join(1);
        visibility.Hidden.Add(2); // entity ids follow join order
        var second = harness.Join(2);
        harness.Ticks(3);
        Assert.DoesNotContain(harness.Transport.IdentitiesFor(1), i => i.EntityId == second.Id);

        visibility.Hidden.Clear();
        harness.Ticks(1);
        visibility.Hidden.Add(second.Id);
        harness.Ticks(1);
        visibility.Hidden.Clear();
        harness.Ticks(1);

        Assert.Equal(2, harness.Transport.IdentitiesFor(1).Count(i => i.EntityId == second.Id));
    }

    [Fact]
    public void A_reconnect_sends_the_names_again()
    {
        var harness = new WorldHarness();
        var player = harness.Join(1);
        harness.Leave(1);
        harness.Ticks(1);

        harness.Join(1);
        harness.Ticks(1);

        Assert.True(harness.Transport.IdentitiesFor(1).Count(i => i.EntityId == player.Id) >= 1);
        Assert.All(harness.Transport.IdentitiesFor(1), i => Assert.StartsWith("c1_", i.Name));
    }

    [Fact]
    public void Monsters_and_players_without_a_name_send_nothing()
    {
        var f = new HitFixture();
        var observer = f.Player(1);
        observer.Interest.Entered.AddRange([observer, f.Monster(2)]);
        var transport = new FakeTransport();

        new GameServer.Net.IdentitySender(transport).Send(observer);

        Assert.Empty(transport.Sent);
    }

    [Fact]
    public void Display_identity_is_the_character_name_in_m1()
    {
        var harness = new WorldHarness();
        var first = harness.Join(1);
        var second = harness.Join(2);

        Assert.Equal("c2_0", DisplayIdentity.For(second, first));
        Assert.Equal("c1_0", DisplayIdentity.For(first, first));
        Assert.Equal(string.Empty, DisplayIdentity.For(new HitFixture().Monster(9), first));
    }

    private sealed class ToggleVisibility : IVisibilityQuery
    {
        public HashSet<uint> Hidden { get; } = [];

        public bool CanSee(Entity observer, Entity target) => !Hidden.Contains(target.Id);
    }
}
