using GameServer.Entities;
using GameServer.Persistence;
using GameServer.Sessions;
using GameServer.Tests.Support;
using GameServer.World;
using Shared.Protocol;

namespace GameServer.Tests.Social;

/// <summary>
/// Local chat (R-6.5-01, spec 9.12): lines go to connected players who have the sender in interest (rule 9) and back
/// to the sender, with the name from DisplayIdentity (rule 8); the per-character rate limit and the text rule refuse
/// with a reason code.
/// </summary>
public class ChatTests
{
    private static WorldHarness Harness(IVisibilityQuery? visibility = null, Shared.Data.Tables.ChatSettings? chat = null) =>
        new(visibility: visibility, chat: chat ?? TestWorlds.Chat());

    /// <summary>A character saved at <paramref name="xCm"/>, so it logs in there (the interest square is ±3000 cm).</summary>
    private static Player JoinAt(WorldHarness harness, int peerId, int xCm)
    {
        harness.Characters.Add(peerId, 0, new CharacterState(1, 0, SessionManager.MainWorldKey, xCm, 0, 0, 0, 100, 10, 100));
        return harness.Join(peerId);
    }

    [Fact]
    public void A_line_reaches_everyone_who_sees_the_sender_and_echoes_to_the_sender()
    {
        var h = Harness();
        var sender = h.Join(1);
        h.Join(2);
        h.Join(3);
        h.Ticks(1);

        h.SendChat(1, "  hello there ");
        h.Ticks(1);

        var expected = (sender.Id, "c1_0", "hello there");
        Assert.Equal([expected], h.Transport.ChatLinesFor(1));
        Assert.Equal([expected], h.Transport.ChatLinesFor(2));
        Assert.Equal([expected], h.Transport.ChatLinesFor(3));
        Assert.Equal(1, h.World.Chat!.Accepted);
        Assert.Equal(3, h.World.ChatDeliveries);
    }

    [Fact]
    public void A_player_outside_interest_gets_nothing()
    {
        var h = Harness();
        var sender = JoinAt(h, 1, 0);
        JoinAt(h, 2, 2_900);
        JoinAt(h, 3, 6_500);
        h.Ticks(1);

        h.SendChat(1, "near only");
        h.Ticks(1);

        Assert.Single(h.Transport.ChatLinesFor(1));
        Assert.Equal([(sender.Id, "c1_0", "near only")], h.Transport.ChatLinesFor(2));
        Assert.Empty(h.Transport.ChatLinesFor(3));
    }

    [Fact]
    public void A_player_who_cannot_see_the_sender_never_gets_its_line()
    {
        var visibility = new HiddenSet();
        var h = Harness(visibility);
        var sender = h.Join(1);
        h.Join(2);
        visibility.Hidden.Add(sender.Id);
        h.Ticks(1);

        h.SendChat(1, "can you see me");
        h.Ticks(1);

        Assert.Empty(h.Transport.ChatLinesFor(2));
        Assert.Single(h.Transport.ChatLinesFor(1)); // the echo: a player always sees itself
    }

    [Fact]
    public void A_hidden_recipient_still_hears_a_sender_it_sees()
    {
        // Delivery follows the recipient's interest, not the sender's.
        var visibility = new HiddenSet();
        var h = Harness(visibility);
        h.Join(1);
        var listener = h.Join(2);
        visibility.Hidden.Add(listener.Id);
        h.Ticks(1);

        h.SendChat(1, "anyone there");
        h.Ticks(1);

        Assert.Single(h.Transport.ChatLinesFor(2));
    }

    [Fact]
    public void The_name_comes_from_DisplayIdentity_for_each_recipient()
    {
        var h = Harness();
        var sender = h.Join(1);
        var listener = h.Join(2);
        h.Ticks(1);

        h.SendChat(1, "hi");
        h.Ticks(1);

        Assert.Equal(GameServer.Social.DisplayIdentity.For(sender, listener), h.Transport.ChatLinesFor(2)[0].Name);
    }

    [Fact]
    public void Lingering_players_get_nothing()
    {
        var h = Harness();
        h.Join(1);
        h.Join(2);
        h.Ticks(1);
        h.Leave(2);
        h.Ticks(1);
        int sentBefore = h.Transport.Sent.Count(s => s.PeerId == 2);

        h.SendChat(1, "hello");
        h.Ticks(1);

        Assert.Equal(sentBefore, h.Transport.Sent.Count(s => s.PeerId == 2));
    }

    [Fact]
    public void Past_the_burst_lines_are_refused_until_the_bucket_refills()
    {
        var h = Harness(chat: TestWorlds.Chat(burst: 3, messagesPerPeriod: 1, periodMs: 1_000));
        h.Join(1);
        h.Ticks(1);

        for (int i = 0; i < 4; i++)
            h.SendChat(1, "line " + i);
        h.Ticks(1);

        Assert.Equal(["line 0", "line 1", "line 2"], h.Transport.ChatLinesFor(1).Select(l => l.Text));
        Assert.Equal([ChatReason.ChatRateLimited], h.Transport.ChatRefusalsFor(1));
        Assert.Equal(1, h.World.Chat!.RateLimited);

        h.Clock.Advance(1_000);
        h.SendChat(1, "after a second");
        h.Ticks(1);

        Assert.Equal("after a second", h.Transport.ChatLinesFor(1)[^1].Text);
    }

    [Fact]
    public void The_rate_limit_survives_a_reconnect()
    {
        var h = new WorldHarness(chat: TestWorlds.Chat(burst: 1, messagesPerPeriod: 1, periodMs: 60_000),
            values: TestSessions.Values(lingerOutOfCombatMs: 30_000));
        var player = h.Join(1);
        h.SendChat(1, "first");
        h.Ticks(1);

        h.Leave(1);
        h.Ticks(1);
        Assert.Same(player, h.Join(1)); // reattached to the lingering character (R-2.1-08)
        h.SendChat(1, "second");
        h.Ticks(1);

        Assert.Equal([ChatReason.ChatRateLimited], h.Transport.ChatRefusalsFor(1));
    }

    [Theory]
    [InlineData("   ", ChatReason.ChatEmpty)]
    [InlineData("two\nlines", ChatReason.ChatInvalidText)]
    public void Bad_text_is_refused_with_its_reason(string text, ChatReason reason)
    {
        var h = Harness();
        h.Join(1);
        h.Join(2);
        h.Ticks(1);

        h.SendChat(1, text);
        h.Ticks(1);

        Assert.Equal([reason], h.Transport.ChatRefusalsFor(1));
        Assert.Empty(h.Transport.ChatLinesFor(1));
        Assert.Empty(h.Transport.ChatRefusalsFor(2));
    }

    [Fact]
    public void A_line_over_max_chars_is_refused_and_one_at_max_is_sent()
    {
        var h = Harness(chat: TestWorlds.Chat(maxChars: 10));
        h.Join(1);
        h.Ticks(1);

        h.SendChat(1, new string('a', 10));
        h.SendChat(1, new string('a', 11));
        h.Ticks(1);

        Assert.Equal([new string('a', 10)], h.Transport.ChatLinesFor(1).Select(l => l.Text));
        Assert.Equal([ChatReason.ChatTooLong], h.Transport.ChatRefusalsFor(1));
    }

    [Fact]
    public void A_line_sent_while_the_character_loads_waits_in_its_session_queue()
    {
        var h = Harness();
        h.Characters.LoadGate = new TaskCompletionSource(); // the character is still loading
        h.Connect(1);
        h.SendChat(1, "early bird");
        h.Ticks(1);
        Assert.Empty(h.Transport.ChatLinesFor(1));

        h.Characters.OpenLoadGate(); // the load finishes now, so the next ticks see it without racing a timer
        h.Ticks(2);

        Assert.True(h.World.TryGetPlayer(1, out _));
        Assert.Equal(["early bird"], h.Transport.ChatLinesFor(1).Select(l => l.Text));
    }

    [Fact]
    public void A_world_without_chat_settings_refuses_lines_at_the_router()
    {
        var h = new WorldHarness();
        h.Join(1);
        h.SendChat(1, "hello");
        h.Ticks(1);

        Assert.Empty(h.Transport.ChatLinesFor(1));
        Assert.Equal(1, h.Router.MalformedRequests);
    }

    [Fact]
    public void Delivery_allocates_nothing_once_warm()
    {
        var h = Harness(chat: TestWorlds.Chat(burst: 1_000, messagesPerPeriod: 1_000));
        h.Join(1);
        h.Join(2);
        h.Ticks(1);
        h.Transport.Record = false;
        h.SendChat(1, "warm up");
        h.Ticks(1);

        var chat = h.World.Chat!;
        var sender = h.World.Entities.OfType<Player>().First();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 20; i++)
        {
            chat.Submit(sender, "steady", h.Clock.UtcNowMs);
            h.World.Tick();
        }

        // The tick itself allocates nothing either (GameWorld allocation tests); chat must not change that.
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private sealed class HiddenSet : IVisibilityQuery
    {
        public HashSet<uint> Hidden { get; } = [];

        public bool CanSee(Entity observer, Entity target) => observer == target || !Hidden.Contains(target.Id);
    }
}
