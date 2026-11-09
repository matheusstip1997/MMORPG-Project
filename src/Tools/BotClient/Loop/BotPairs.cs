using System.Collections.Concurrent;

namespace BotClient.Loop;

/// <summary>
/// Which bot plays the other side of a pair step (<c>trade</c>; M4 plan section 1 "the bots play the M4 loop in pairs") and
/// that bot's entity, for the bots of one BotClient process. Each pair step publishes its own entity once known.
/// <para>
/// Placeholder rule until WP-M4-12's pair plumbing (<c>BotScenario.pairs</c>, built in parallel) lands: bot <c>n</c> pairs with
/// <c>n ^ 1</c> (0 with 1, 2 with 3: "bot n with bot n+1"). WP-M4-12 replaces <see cref="PartnerOf"/> and nothing else.
/// </para>
/// </summary>
public static class BotPairs
{
    private static readonly ConcurrentDictionary<int, uint> Entities = new();

    /// <summary>The partner of bot <paramref name="botIndex"/>.</summary>
    public static int PartnerOf(int botIndex) => botIndex ^ 1;

    /// <summary>The lower index of a pair leads (invites, proposes).</summary>
    public static bool Leads(int botIndex) => botIndex < PartnerOf(botIndex);

    /// <summary>Bot <paramref name="botIndex"/> is entity <paramref name="entityId"/> on the server now.</summary>
    public static void Publish(int botIndex, uint entityId) => Entities[botIndex] = entityId;

    /// <summary>The entity of bot <paramref name="botIndex"/>, 0 while it has not published one.</summary>
    public static uint EntityOf(int botIndex) => Entities.TryGetValue(botIndex, out uint entityId) ? entityId : 0;

    /// <summary>Forgets every published entity (a new run in the same process: tests).</summary>
    public static void Clear() => Entities.Clear();
}
