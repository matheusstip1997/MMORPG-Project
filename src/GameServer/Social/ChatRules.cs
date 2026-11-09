using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;

namespace GameServer.Social;

/// <summary>
/// The chat tables the world reads (R-6.5-01, spec 9.12; WP-M4-2): the line length and queue bound
/// (<see cref="ChatSettings"/>) and one rate limit per channel (<see cref="ChatChannelDef"/>, indexed by
/// <see cref="ChatChannel"/>).
/// </summary>
public sealed class ChatRules
{
    public ChatRules(ChatSettings settings, ChatChannelDef[] channels)
    {
        if (channels.Length <= (int)ChatCodec.LastChannel)
            throw new ArgumentException("one ChatChannelDef row per channel", nameof(channels));
        for (int i = 0; i < channels.Length; i++)
        {
            if (channels[i] == null)
                throw new ArgumentException($"no ChatChannelDef row for channel {(ChatChannel)i}", nameof(channels));
        }
        Settings = settings;
        Channels = channels;
    }

    public ChatSettings Settings { get; }

    /// <summary>By <see cref="ChatChannel"/> value.</summary>
    public ChatChannelDef[] Channels { get; }

    /// <summary>Chat lines one session may have queued for the next tick.</summary>
    public int MaxQueuedLines => Settings.MaxQueuedLines;

    /// <summary>From tables that passed validation.</summary>
    public static ChatRules From(ConfigSet set) => new(set.Single<ChatSettings>(), ChatChannelDef.ByChannel(set));
}
