namespace GameServer.Social;

/// <summary>
/// What other systems read of the mutes and blocks (spec 6.5 "M4 social", WP-M4-16): chat (<c>ChatService</c>, <c>ChatSender</c>),
/// party invites (<c>PartyService</c>) and trade invites (<c>TradeService</c>). Only <see cref="SocialService"/> changes them.
/// Committed state only (rule 5). World thread only; nothing here allocates.
/// </summary>
public interface ISocialQuery
{
    /// <summary><paramref name="recipientId"/> mutes or blocks <paramref name="senderId"/>: the sender's chat lines never reach it.</summary>
    bool HidesChatFrom(long recipientId, long senderId);

    /// <summary><paramref name="blockerId"/> blocks <paramref name="targetId"/>: the target's whispers and invites to it are refused.</summary>
    bool Blocks(long blockerId, long targetId);
}

/// <summary>No mutes or blocks: a world built without <c>SocialSettings</c> (tests, tools).</summary>
public sealed class NoSocial : ISocialQuery
{
    public static readonly NoSocial Instance = new();

    public bool HidesChatFrom(long recipientId, long senderId) => false;

    public bool Blocks(long blockerId, long targetId) => false;
}
