using GameServer.Entities;
using GameServer.Social;
using MemoryPack;
using Shared.Protocol;

namespace GameServer.Net;

/// <summary>
/// Sends the social traffic in tick phase 10 (spec 6.5 "M4 social", WP-M4-16): a connected player's answers and notices
/// (<see cref="SocialResult"/>), then its whole lists (<see cref="SocialState"/>) when they changed, a friend came or went, or the
/// connection is new. A state is built only on a change, so a quiet tick sends and allocates nothing.
/// </summary>
internal sealed class SocialSender
{
    private static readonly MemoryPackSerializerOptions Options = MemoryPackSerializerOptions.Utf8;

    private readonly ITransport _transport;
    private readonly ReliableMessageWriter _writer = new(512);

    public SocialSender(ITransport transport) => _transport = transport;

    /// <summary>States sent since the world started (tests).</summary>
    public long StatesSent { get; private set; }

    public void Send(Player player, SocialService social, long nowMs)
    {
        var answers = player.Social;
        foreach (var result in answers.Results)
            Write(player, ReliableMessageId.SocialResult, result);
        answers.Results.Clear();
        if (!answers.Dirty)
            return;
        var state = social.BuildState(player.CharacterId, nowMs);
        state.Version = ++answers.Version;
        Write(player, ReliableMessageId.SocialState, state);
        answers.Dirty = false;
        StatesSent++;
    }

    private void Write<T>(Player player, ReliableMessageId id, T message) where T : class
    {
        _writer.Begin(id);
        MemoryPackSerializer.Serialize(_writer, message, Options);
        _transport.Send(player.PeerId, Channel.ReliableOrdered, _writer.Written);
    }
}
