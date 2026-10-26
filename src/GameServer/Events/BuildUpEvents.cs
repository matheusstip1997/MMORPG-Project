namespace GameServer.Events;

// Build-up and ground area events (spec 3.8 "emits BuildupTriggered", spec 3.1 ground areas; spec 8.1). Build-up IDs are
// BuildUpDef net IDs, status IDs StatusEffectDef net IDs, action IDs ActionDef net IDs. Area IDs are the world's ground
// area IDs (uint, as in the ground area hot messages), not entity IDs. The envelope's ActorCharacterId is the character
// that filled the meter or placed the area (0 for monsters).

/// <summary>
/// A build-up meter of <see cref="TargetId"/> filled (R-3.8-02) on a hit of <see cref="SourceId"/>, which triggered its
/// status (<see cref="StatusId"/>, credited to the source, R-3.8-09). Published by the BuildUpSystem in phase 4 or 5, before
/// the status's <c>EffectApplied</c>.
/// </summary>
public readonly record struct BuildupTriggered(uint TargetId, uint SourceId, int BuildUpId, int StatusId) : IGameEvent;

/// <summary>A ground area was placed (spec 3.1): its action and effect index (the ground area spawn message) and its start tick.</summary>
public readonly record struct GroundAreaSpawned(uint AreaId, uint OwnerId, int ActionId, int EffectIndex, long StartTick) : IGameEvent;
