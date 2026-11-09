using GameServer.Entities;
using GameServer.Social;

namespace GameServer.World;

/// <summary>
/// R-2.4-03 "party members always see each other" (spec 6.5 "M4 party", M4 decision 1; WP-M4-2): two players of one party
/// are visible to each other whatever the inner query says (hiding, disguise and Sense vs Detect come later, spec 2.4);
/// every other pair is the inner query's. Inert in play while the inner query is <see cref="EveryoneVisible"/>.
/// </summary>
public sealed class PartyVisibility : IVisibilityQuery
{
    private readonly IVisibilityQuery _inner;
    private readonly IPartyQuery _parties;

    public PartyVisibility(IVisibilityQuery inner, IPartyQuery parties)
    {
        _inner = inner;
        _parties = parties;
    }

    /// <summary>The inner query with the party rule over it; the inner query alone when the world has no parties.</summary>
    public static IVisibilityQuery Wrap(IVisibilityQuery inner, IPartyQuery? parties) =>
        parties == null ? inner : new PartyVisibility(inner, parties);

    public bool CanSee(Entity observer, Entity target) =>
        (observer is Player watcher && target is Player member && _parties.SameParty(watcher.CharacterId, member.CharacterId))
        || _inner.CanSee(observer, target);
}
