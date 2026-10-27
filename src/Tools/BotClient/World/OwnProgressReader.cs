namespace BotClient.World;

/// <summary>
/// Reads the bot's level and exp (spec 3.6) into <see cref="BotOwnState"/> for the <c>m2-exit</c> level report.
/// Placeholder: the protocol has no own-entity level yet, so the report prints "level not in protocol yet" and the level
/// criterion fails. TODO(WP-M2-3): read the own-entity exp and level message here (override
/// <see cref="IBotOwnReader.OnReliable"/> or <see cref="IBotOwnReader.OnSnapshot"/>, set <c>own.Level</c> and
/// <c>own.Exp</c>) and set <see cref="LevelInProtocol"/> to true; nothing else changes.
/// </summary>
public sealed class OwnProgressReader : IBotOwnReader
{
    /// <summary>The protocol carries the own level (what the level report says when no bot has one).</summary>
    public const bool LevelInProtocol = false;
}
