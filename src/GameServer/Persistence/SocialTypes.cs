using Shared.Protocol;

namespace GameServer.Persistence;

/// <summary>One friend of a character as loaded (realm V0024 <c>friends</c>): the other character and its name as this one saw it.</summary>
public sealed record FriendRow(long OtherId, string Name);

/// <summary>One muted or blocked player of a character as loaded (realm V0024 <c>blocks</c>).</summary>
public sealed record BlockRow(long TargetId, SocialBlockKind Kind, string Name);

/// <summary>A character's friends and block list as read at login (WP-M4-16), each in id order.</summary>
public sealed record SocialRecord(IReadOnlyList<FriendRow> Friends, IReadOnlyList<BlockRow> Blocks)
{
    public static readonly SocialRecord Empty = new(Array.Empty<FriendRow>(), Array.Empty<BlockRow>());
}

/// <summary>What one social operation writes (WP-M4-16). Also the <c>op</c> of the <c>SocialChanged</c> telemetry: append only.</summary>
public enum SocialOpKind : byte
{
    None = 0,
    /// <summary>A friendship formed: the <c>friends</c> row of the pair.</summary>
    FriendAdd = 1,
    /// <summary>A friendship ended: its row deleted.</summary>
    FriendRemove = 2,
    /// <summary>A mute or a block set, or changed from one kind to the other.</summary>
    BlockSet = 3,
    /// <summary>A mute or a block lifted.</summary>
    BlockRemove = 4,
}

/// <summary>
/// One social operation in a character save (WP-M4-16): <see cref="CharacterId"/> is the character whose save carries it (the
/// one who accepted, removed, muted or blocked), <see cref="OtherId"/> the other character. A friendship carries both names, each
/// as the other side saw it: <see cref="OtherName"/> (the other as <see cref="CharacterId"/> sees it) and <see cref="OwnName"/>
/// (<see cref="CharacterId"/> as the other sees it). A block carries the target's name and its kind.
/// </summary>
public sealed record SocialOpRow(SocialOpKind Kind, long CharacterId, long OtherId, string OtherName = "", string OwnName = "",
    SocialBlockKind BlockKind = SocialBlockKind.None);

/// <summary>The social part of a character save (WP-M4-16): the operations in the order they were made.</summary>
public sealed record SocialBatch(IReadOnlyList<SocialOpRow> Ops)
{
    public bool IsEmpty => Ops.Count == 0;
}

/// <summary>
/// Hands a character's pending social operations to the save being issued and learns how that save ended. Implemented by
/// <c>Social/SocialService</c>; called on the world thread by <see cref="SaveScheduler"/>.
/// </summary>
public interface ISocialBatchSource
{
    SocialBatch? TakeBatch(long characterId, Guid saveId);

    void Committed(long characterId, Guid saveId);
}
