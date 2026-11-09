using GameServer.Entities;
using GameServer.Persistence;
using GameServer.Sessions;
using GameServer.Tests.Support;
using Shared.Data.Tables;
using Shared.Protocol;

namespace GameServer.Tests.Social;

/// <summary>
/// Chat channels (spec 6.5 "M4 party", 9.12; WP-M4-2): a party line reaches the sender's party members wherever they are and
/// nobody else; a whisper reaches the one player whose display name the sender typed, and the echo names the recipient; each
/// channel has its own rate limit (<see cref="ChatChannelDef"/>); local <c>/</c> lines still go to the dev console path.
/// </summary>
public class ChatChannelTests
{
    private static WorldHarness Harness(Shared.Data.Tables.ChatChannelDef[]? channels = null) =>
        new(chat: TestWorlds.Chat(channels: channels), party: TestWorlds.Party());

    private static Player JoinAt(WorldHarness harness, int peerId, int xCm)
    {
        harness.Characters.Add(peerId, 0, new CharacterState(1, 0, SessionManager.MainWorldKey, xCm, 0, 0, 0, 100, 10, 100));
        return harness.Join(peerId);
    }

    private static void Form(WorldHarness h, int leader, int member)
    {
        h.SendParty(leader, 100 + (uint)member, PartyAction.Invite, targetName: $"c{member}_0");
        h.Ticks(1);
        h.SendParty(member, 200 + (uint)member, PartyAction.Accept, h.Transport.PartyInvitesFor(member)[^1].InviteId);
        h.Ticks(1);
    }

    private static ChatChannelDef Row(ChatChannel channel, int burst, int perPeriod = 1, int periodMs = 60_000) => new()
    {
        Channel = Shared.Data.ConfigTableReader.CamelCase(channel.ToString()), Burst = burst, MessagesPerPeriod = perPeriod, PeriodMs = periodMs,
    };

    [Fact]
    public void A_party_line_reaches_every_member_wherever_and_nobody_else()
    {
        var h = Harness();
        var a = JoinAt(h, 1, 0);
        JoinAt(h, 2, 9_000); // a member out of interest
        JoinAt(h, 3, 500); // a stranger next to the sender
        h.Ticks(1);
        Form(h, 1, 2);

        h.SendChat(1, "pull now", ChatChannel.Party);
        h.Ticks(1);

        foreach (int peer in new[] { 1, 2 })
        {
            var line = Assert.Single(h.Transport.ChatMessagesFor(peer));
            Assert.Equal((a.Id, "c1_0", "pull now", ChatChannel.Party, ""), (line.SenderEntityId, line.SenderName, line.Text, line.Channel, line.TargetName));
        }
        Assert.Empty(h.Transport.ChatMessagesFor(3));
    }

    [Fact]
    public void A_party_line_without_a_party_is_refused()
    {
        var h = Harness();
        h.Join(1);
        h.Ticks(1);

        h.SendChat(1, "anyone?", ChatChannel.Party);
        h.Ticks(1);

        Assert.Equal([ChatReason.ChatNotInParty], h.Transport.ChatRefusalsFor(1));
        Assert.Empty(h.Transport.ChatMessagesFor(1));
    }

    [Fact]
    public void A_whisper_reaches_one_player_by_display_name_and_the_echo_names_it()
    {
        var h = Harness();
        var a = JoinAt(h, 1, 0);
        JoinAt(h, 2, 9_000);
        JoinAt(h, 3, 300);
        h.Ticks(1);

        h.SendChat(1, "psst", ChatChannel.Whisper, "C2_0"); // names ignore case
        h.Ticks(1);

        var received = Assert.Single(h.Transport.ChatMessagesFor(2));
        Assert.Equal((a.Id, "c1_0", "psst", ChatChannel.Whisper, ""), (received.SenderEntityId, received.SenderName, received.Text, received.Channel, received.TargetName));
        var echo = Assert.Single(h.Transport.ChatMessagesFor(1));
        Assert.Equal((ChatChannel.Whisper, "c2_0"), (echo.Channel, echo.TargetName));
        Assert.Empty(h.Transport.ChatMessagesFor(3));
    }

    [Fact]
    public void A_whisper_to_nobody_or_to_oneself_is_refused()
    {
        var h = Harness();
        h.Join(1);
        h.Ticks(1);

        h.SendChat(1, "hello?", ChatChannel.Whisper, "ghost");
        h.SendChat(1, "me", ChatChannel.Whisper, "c1_0");
        h.SendChat(1, "blank", ChatChannel.Whisper);
        h.Ticks(1);

        Assert.Equal([ChatReason.ChatTargetNotFound, ChatReason.ChatTargetSelf, ChatReason.ChatTargetNotFound], h.Transport.ChatRefusalsFor(1));
    }

    [Fact]
    public void Each_channel_has_its_own_rate_limit()
    {
        var h = Harness([Row(ChatChannel.Local, 1), Row(ChatChannel.Party, 2, 2), Row(ChatChannel.Whisper, 1)]);
        h.Join(1);
        h.Join(2);
        h.Ticks(1);
        Form(h, 1, 2);

        h.SendChat(1, "local one");
        h.SendChat(1, "local two"); // the local bucket is empty
        h.SendChat(1, "party one", ChatChannel.Party);
        h.SendChat(1, "party two", ChatChannel.Party);
        h.SendChat(1, "whisper", ChatChannel.Whisper, "c2_0");
        h.Ticks(1);

        Assert.Equal([ChatReason.ChatRateLimited], h.Transport.ChatRefusalsFor(1));
        Assert.Equal(["local one", "party one", "party two", "whisper"], h.Transport.ChatMessagesFor(2).Select(l => l.Text));
    }
}
