namespace GameServer.Events;

// TODO(WP-M3-1): delete this file when WP-M3-1's Events/CraftingEvents.cs is on main; it owns the activity event records.
// Until then the demand loop's node counters (WP-M3-4) consume this stand-in with the shape the orchestrator gave, and
// tests publish it. A second definition of Harvested fails the build, so the stand-in cannot outlive the real record.

/// <summary>
/// A player harvested a resource node (spec 8.1 <c>Harvested</c>, spec 5.2; published by harvesting, WP-M3-5, after its
/// commit). <see cref="NodeDefId"/> is the <c>NodeDef.netId</c>, <see cref="RegionId"/> the node's region index,
/// <see cref="Tier"/> the node tier and <see cref="OperationKey"/> the harvest's idempotency key.
/// </summary>
public readonly record struct Harvested(long CharacterId, int JobId, int NodeDefId, int RegionId, int Tier, string OperationKey)
    : IGameEvent;
