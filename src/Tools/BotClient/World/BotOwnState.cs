using Game.Gameplay.Entities;
using Shared.Protocol;

namespace BotClient.World;

/// <summary>
/// A bot's own progress as its client knows it (WP-M2-12): level, exp and gold, each unknown until an
/// <see cref="IBotOwnReader"/> reads it from the protocol. Written on the bots' thread, read by the load test's level
/// report on another, so every value is stored atomically (-1: unknown).
/// </summary>
public sealed class BotOwnState
{
    private int _level = -1;
    private long _exp = -1;
    private long _gold = -1;

    public int? Level
    {
        get => Volatile.Read(ref _level) is var level and >= 0 ? level : null;
        set => Volatile.Write(ref _level, value ?? -1);
    }

    public long? Exp
    {
        get => Interlocked.Read(ref _exp) is var exp and >= 0 ? exp : null;
        set => Interlocked.Exchange(ref _exp, value ?? -1);
    }

    public long? Gold
    {
        get => Interlocked.Read(ref _gold) is var gold and >= 0 ? gold : null;
        set => Interlocked.Exchange(ref _gold, value ?? -1);
    }
}

/// <summary>
/// Fills <see cref="BotOwnState"/> from what the server sends this bot: a reliable message, or the own part of a snapshot
/// (after ClientCore's <see cref="EntityRegistry"/> applied it). Both default to nothing, so a reader implements the one
/// its message needs. Runs on the bots' thread.
/// </summary>
public interface IBotOwnReader
{
    void OnReliable(ReliableMessageId id, ReadOnlySpan<byte> message, BotOwnState own)
    {
    }

    void OnSnapshot(EntityRegistry registry, BotOwnState own)
    {
    }
}
