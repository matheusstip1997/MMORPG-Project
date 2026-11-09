using Shared.Protocol;

namespace GameServer.Events;

// Social events (spec 6.5, 8.1; WP-M4-2), emitted by Social/PartyService in phase 1. The envelope's ActorCharacterId is the
// character the change is about.

/// <summary>
/// A party changed (spec 8.1 <c>PartyChanged</c>): <see cref="CharacterId"/> formed it, joined, left, was kicked, became the
/// leader, or (for <see cref="PartyChangeKind.Disbanded"/>) was the last member when it dissolved. <see cref="Members"/> is
/// the party after the change, in join order (empty once disbanded); a copy, since parties change rarely.
/// </summary>
public readonly record struct PartyChanged(long PartyId, long CharacterId, PartyChangeKind Change, long[] Members) : IGameEvent;

/// <summary>The second member joined: a party now exists (spec 8.1 <c>GroupFormed</c>).</summary>
public readonly record struct GroupFormed(long PartyId, long LeaderCharacterId, long MemberCharacterId) : IGameEvent;
