using GameServer.Entities;
using Shared.Data.Tables;
using Shared.Protocol;

namespace GameServer.Social;

/// <summary>
/// A checked chat line waiting for phase 10: its channel and, for a whisper, the one recipient (WP-M4-2).
/// </summary>
internal readonly record struct PendingChatLine(Player Sender, string Text, ChatChannel Channel = ChatChannel.Local, Player? Recipient = null);

/// <summary>A refused chat line: the sender gets the reason in phase 10.</summary>
internal readonly record struct PendingChatRefusal(Player Sender, ChatReason Reason);

/// <summary>
/// Chat (R-6.5-01 "chat", spec 6.5 <c>Chat</c>, spec 9.12; channels WP-M4-2): checks each line a connected player sent and
/// keeps the accepted lines and the refusals for <see cref="GameServer.Net.ChatSender"/>, which delivers them in phase 10.
/// World thread only.
/// <list type="bullet">
/// <item>Rate limit first: a per-character, per-channel <see cref="GameServer.Net.Security.TokenBucket"/> (the channel's
/// <see cref="ChatChannelDef"/> row: burst, then <c>messagesPerPeriod</c> per <c>periodMs</c>); every line counts, so refusals
/// cannot be spammed either.</item>
/// <item>Then the text rule shared with the client (<see cref="ChatText"/>, <c>ChatSettings.maxChars</c>); the trimmed text is sent.</item>
/// <item>Local lines starting with <c>/</c> go to the dev console (WP-M3-13), never to chat; the client reads the party and
/// whisper commands itself and sends them as requests or channel lines.</item>
/// <item>Party: the sender must be in a party (<see cref="ChatReason.ChatNotInParty"/>); the line reaches its members.
/// Whisper: the recipient is the connected player whose display name, as the sender sees it, is the target name
/// (<see cref="ChatReason.ChatTargetNotFound"/>, <see cref="ChatReason.ChatTargetSelf"/>; rule 8).</item>
/// </list>
/// TODO(open): the word filter (R-6.5-01 "filter") is not decided. TODO(open) M4 decision 16: friends, mute and block, guild
/// and global channels. TODO(later): chat logs for moderation tooling (spec 10).
/// </summary>
internal sealed class ChatService
{
    private readonly ChatSettings _settings;
    private readonly ChatChannelDef[] _channels;

    public ChatService(ChatSettings settings, ChatChannelDef[] channels)
    {
        if (channels.Length <= (int)ChatCodec.LastChannel)
            throw new ArgumentException("one ChatChannelDef row per channel", nameof(channels));
        _settings = settings;
        _channels = channels;
    }

    /// <summary>The parties a party line reaches (WP-M4-2); none by default.</summary>
    public IPartyQuery Parties { get; set; } = NoParties.Instance;

    /// <summary>The connected player whose display name, as the sender sees it, is the name; null for none (WP-M4-2).</summary>
    public Func<Player, string, Player?> FindByName { get; set; } = (_, _) => null;

    public List<PendingChatLine> Lines { get; } = new();

    public List<PendingChatRefusal> Refusals { get; } = new();

    /// <summary>Lines accepted since the world started (telemetry <c>ChatCounters</c>).</summary>
    public long Accepted { get; private set; }

    /// <summary>Lines refused for any reason, rate limit included.</summary>
    public long Refused { get; private set; }

    /// <summary>Lines refused by the rate limit (R-2.5-06: rate-limit hits are counted).</summary>
    public long RateLimited { get; private set; }

    /// <summary>Phase 1: one local line from a connected player, at <paramref name="nowMs"/> (UTC ms from the world's clock).</summary>
    public void Submit(Player sender, string text, long nowMs) => Submit(sender, ChatChannel.Local, text, string.Empty, nowMs);

    /// <summary>Phase 1: one line on <paramref name="channel"/> (a whisper to <paramref name="targetName"/>).</summary>
    public void Submit(Player sender, ChatChannel channel, string text, string targetName, long nowMs)
    {
        if (channel > ChatCodec.LastChannel)
            channel = ChatChannel.Local; // the reader refuses it before; a defensive default
        var row = _channels[(int)channel];
        if (!sender.ChatBuckets[(int)channel].TryTake(nowMs, row.Burst, row.MessagesPerPeriod, row.PeriodMs))
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
        Player? recipient = null;
        switch (channel)
        {
            case ChatChannel.Local when Dev.Commands.DevCommandParser.IsCommand(clean):
                // A dev console line (spec 11.7, WP-M3-13): never broadcast; the world hands it to the console in phase 1.
                Commands.Add(new PendingChatLine(sender, clean));
                return;
            case ChatChannel.Party when Parties.PartyIdOf(sender.CharacterId) == 0:
                Refuse(sender, ChatReason.ChatNotInParty);
                return;
            case ChatChannel.Whisper:
                recipient = string.IsNullOrWhiteSpace(targetName) ? null : FindByName(sender, targetName.Trim());
                if (recipient == null)
                {
                    Refuse(sender, ChatReason.ChatTargetNotFound);
                    return;
                }
                if (recipient == sender)
                {
                    Refuse(sender, ChatReason.ChatTargetSelf);
                    return;
                }
                break;
        }
        Accepted++;
        Lines.Add(new PendingChatLine(sender, clean, channel, recipient));
    }

    /// <summary>
    /// Checked lines starting with <c>/</c>, under the same rate limit and text rule, for the dev console (WP-M3-13); the
    /// world takes them in phase 1, right after <see cref="Submit(Player, string, long)"/>.
    /// </summary>
    public List<PendingChatLine> Commands { get; } = new();

    /// <summary>Phase 10, after delivery.</summary>
    public void Clear()
    {
        Lines.Clear();
        Refusals.Clear();
        Commands.Clear();
    }

    private void Refuse(Player sender, ChatReason reason)
    {
        Refused++;
        Refusals.Add(new PendingChatRefusal(sender, reason));
    }
}
