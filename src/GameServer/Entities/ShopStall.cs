using Shared.Protocol;
using Shared.Simulation;

namespace GameServer.Entities;

/// <summary>
/// A player's shop stall (spec 4.5 "M4 stalls", M4 decision 7; WP-M4-8): an entity on a <c>stall</c> marker of a city while the
/// stall is open, built and removed by <c>StallService</c> (the only writer of stalls). Its spawn event carries the place ordinal
/// as its definition id, so a client knows which place it stands on; its owner's name reaches observers only as an
/// <c>EntityIdentity</c> through <c>DisplayIdentity</c> (rule 8), from the name stored with the stall, so a stall whose owner is
/// offline still shows it. Never a combatant, never a lock target.
/// </summary>
public sealed class ShopStall : Entity
{
    public ShopStall(uint id, string shopId, long ownerCharacterId, string ownerName, int placeOrdinal, string markerId,
        long openedUtcMs, long closesUtcMs, MovementState movement, int heightCm)
        : base(id, EntityKind.ShopStall, movement, heightCm)
    {
        ShopId = shopId;
        OwnerCharacterId = ownerCharacterId;
        OwnerName = ownerName;
        PlaceOrdinal = placeOrdinal;
        MarkerId = markerId;
        OpenedUtcMs = openedUtcMs;
        ClosesUtcMs = closesUtcMs;
        DefId = (ushort)placeOrdinal;
    }

    /// <summary>The stall's id: its <c>shops</c> row, and the <c>stallId</c> of its listings.</summary>
    public string ShopId { get; }

    public long OwnerCharacterId { get; }

    /// <summary>The owner's character name when the stall opened (shown through <c>DisplayIdentity</c> only).</summary>
    internal string OwnerName { get; }

    /// <summary>The stall place's ordinal (<c>Shared.World.StallPlaces</c>).</summary>
    public int PlaceOrdinal { get; }

    /// <summary>The stall marker it stands on.</summary>
    public string MarkerId { get; }

    public long OpenedUtcMs { get; }

    /// <summary>When the stall closes (UTC ms); every listing on it expires at this time.</summary>
    public long ClosesUtcMs { get; }
}
