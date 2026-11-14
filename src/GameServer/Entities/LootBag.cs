using Shared.Protocol;
using Shared.Simulation;

namespace GameServer.Entities;

/// <summary>
/// A player's death loot bag lying at the death spot (spec 4.3 R-4.3-03, <c>LootBag</c>; WP-M4-9). The entity is only what the
/// world shows: interest and <c>IVisibilityQuery</c> like any entity (architecture rule 9), spawned once the death's save
/// committed and removed when the bag is emptied or expires. Its contents, owner, window and expiry are kept by the death
/// loot service (<c>Items/LootBags/DeathLootService</c>) and persisted in <c>loot_bags</c>, so a bag survives a restart (it is
/// spawned again at world start with a new entity id). The spawn event's definition id is 0: every bag looks the same, and
/// the owner is never on the wire (rule 8).
/// </summary>
public sealed class LootBag : Entity
{
    public LootBag(uint id, string bagId, MovementState movement, int heightCm)
        : base(id, EntityKind.LootBag, movement, heightCm)
    {
        BagId = bagId;
    }

    /// <summary>The bag's id (the death's operation key, unique for ever).</summary>
    public string BagId { get; }
}
