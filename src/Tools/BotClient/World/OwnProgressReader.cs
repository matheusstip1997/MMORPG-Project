using Shared.Protocol;

namespace BotClient.World;

/// <summary>
/// Reads the bot's level and exp (spec 3.6) into <see cref="BotOwnState"/> for the <c>m2-exit</c> level report, from the
/// reliable <see cref="ProgressionState"/> the server sends at enter world and on every change (protocol 18, WP-M2-3).
/// </summary>
public sealed class OwnProgressReader : IBotOwnReader
{
    /// <summary>The protocol carries the own level (what the level report says when no bot has one).</summary>
    public const bool LevelInProtocol = true;

    public void OnReliable(ReliableMessageId id, ReadOnlySpan<byte> message, BotOwnState own)
    {
        if (id != ReliableMessageId.ProgressionState || !ReliableMessages.TryRead(message, out ProgressionState state))
            return;
        own.Level = state.Level;
        own.Exp = state.Exp;
    }
}
