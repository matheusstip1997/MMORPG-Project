namespace GameServer.Events;

/// <summary>
/// A consumable was used (spec 8.1 <c>ItemUsed</c>; spec 3.4 consumable use, WP-M3-3): <see cref="Count"/> units of
/// <see cref="ItemId"/> (<c>ItemDef.id</c>) left the bag at a use action's effect point under <see cref="OperationKey"/>.
/// Published by <c>InventoryService</c> once per operation key, right after the <c>ItemDestroyed</c> of the save that holds
/// the removal committed (rule 5); the actor is the character. Consumers (job XP, achievements) are idempotent per event id.
/// TODO(WP-M3-1): stand-in with the agreed shape; WP-M3-1 owns the activity event records (<c>Events/CraftingEvents.cs</c>).
/// Whichever of the two merges second keeps that file's record and deletes this one.
/// </summary>
public readonly record struct ItemUsed(long CharacterId, string ItemId, int Count, string OperationKey) : IGameEvent;
