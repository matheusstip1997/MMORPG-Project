using Shared.Protocol;

namespace BotClient.World;

/// <summary>
/// Reads the bot's committed gold into <see cref="BotOwnState"/> from the reliable <see cref="GoldBalance"/> the server sends
/// at enter world and with every bag change (protocol 17, WP-M2-4; spec 4.2). QA M2 wave 0 F2: without it the gold stayed unknown.
/// </summary>
public sealed class OwnGoldReader : IBotOwnReader
{
    public void OnReliable(ReliableMessageId id, ReadOnlySpan<byte> message, BotOwnState own)
    {
        if (id != ReliableMessageId.GoldBalance || !ReliableMessages.TryRead(message, out GoldBalance balance))
            return;
        own.Gold = balance.Gold;
    }
}
