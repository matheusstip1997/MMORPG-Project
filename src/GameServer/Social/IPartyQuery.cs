namespace GameServer.Social;

/// <summary>
/// What other systems read of the parties (spec 6.5 "M4 party", M4 decision 1; WP-M4-2): exp shares (<c>ExpService</c>), runs
/// (<c>QuestService</c>), party immunity (<c>PvpRules</c>), visibility (<c>PartyVisibility</c>), chat, and later the party loot
/// roll (WP-M4-6) and the death bag (WP-M4-9). Only <see cref="PartyService"/> changes parties. World thread only; nothing
/// here allocates.
/// </summary>
public interface IPartyQuery
{
    /// <summary>The character's party, 0 when it is in none.</summary>
    long PartyIdOf(long characterId);

    /// <summary>Whether two different characters are in one party (false for a character and itself, and for 0).</summary>
    bool SameParty(long characterA, long characterB);

    /// <summary>The party's leader, 0 for no such party.</summary>
    long LeaderOf(long partyId);

    /// <summary>The party's members in join order into <paramref name="into"/> (at most its length); returns how many were written.</summary>
    int MembersOf(long partyId, Span<long> into);

    /// <summary>The party's member count, 0 for no such party.</summary>
    int MemberCount(long partyId);
}

/// <summary>No parties: a world built without <c>PartySettings</c> (tests, tools).</summary>
public sealed class NoParties : IPartyQuery
{
    public static readonly NoParties Instance = new();

    public long PartyIdOf(long characterId) => 0;

    public bool SameParty(long characterA, long characterB) => false;

    public long LeaderOf(long partyId) => 0;

    public int MembersOf(long partyId, Span<long> into) => 0;

    public int MemberCount(long partyId) => 0;
}
