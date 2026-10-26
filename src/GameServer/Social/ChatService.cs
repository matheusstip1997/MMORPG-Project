using GameServer.Entities;
using Shared.Data.Tables;
using Shared.Protocol;

namespace GameServer.Social;

/// <summary>A checked chat line waiting for phase 10.</summary>
internal readonly record struct PendingChatLine(Player Sender, string Text);

/// <summary>A refused chat line: the sender gets the reason in phase 10.</summary>
internal readonly record struct PendingChatRefusal(Player Sender, ChatReason Reason);

/// <summary>
/// Local chat (R-6.5-01 "chat", spec 6.5 <c>Chat</c>, spec 9.12): checks each line a connected player sent and keeps the
/// accepted lines and the refusals for <see cref="GameServer.Net.ChatSender"/>, which delivers them in phase 10. World
/// thread only.
/// <list type="bullet">
/// <item>Rate limit first: a per-character <see cref="GameServer.Net.Security.TokenBucket"/> (<see cref="ChatSettings"/>
/// burst, then <c>messagesPerPeriod</c> per <c>periodMs</c>); every line counts, so refusals cannot be spammed either.</item>
/// <item>Then the text rule shared with the client (<see cref="ChatText"/>, <c>maxChars</c>); the trimmed text is sent.</item>
/// </list>
/// TODO(open): the word filter (R-6.5-01 "filter") is not decided. TODO(M2): mute and block, party, guild, whisper and
/// global channels. TODO(later): chat logs for moderation tooling (spec 10).
/// </summary>
internal sealed class ChatService
{
    private readonly ChatSettings _settings;

    public ChatService(ChatSettings settings)
    {
        _settings = settings;
    }

    public List<PendingChatLine> Lines { get; } = new();

    public List<PendingChatRefusal> Refusals { get; } = new();

    /// <summary>Lines accepted since the world started (telemetry <c>ChatCounters</c>).</summary>
    public long Accepted { get; private set; }

    /// <summary>Lines refused for any reason, rate limit included.</summary>
    public long Refused { get; private set; }

    /// <summary>Lines refused by the rate limit (R-2.5-06: rate-limit hits are counted).</summary>
    public long RateLimited { get; private set; }

    /// <summary>Phase 1: one line from a connected player, at <paramref name="nowMs"/> (UTC ms from the world's clock).</summary>
    public void Submit(Player sender, string text, long nowMs)
    {
        if (!sender.ChatBucket.TryTake(nowMs, _settings.Burst, _settings.MessagesPerPeriod, _settings.PeriodMs))
        {
            RateLimited++;
            Refuse(sender, ChatReason.ChatRateLimited);
            return;
        }
        var reason = ChatText.Check(text, _settings.MaxChars, out var clean);
        if (reason != ChatReason.None)
        {
            Refuse(sender, reason);
            return;
        }
        Accepted++;
        Lines.Add(new PendingChatLine(sender, clean));
    }

    /// <summary>Phase 10, after delivery.</summary>
    public void Clear()
    {
        Lines.Clear();
        Refusals.Clear();
    }

    private void Refuse(Player sender, ChatReason reason)
    {
        Refused++;
        Refusals.Add(new PendingChatRefusal(sender, reason));
    }
}
