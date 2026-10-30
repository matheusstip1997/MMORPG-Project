using Shared.Simulation.Jobs;

namespace GameServer.Events;

// Job activity events (spec 5.1-5.3, 8.1; the records are WP-M3-1's, so producers and consumers can be built in parallel).
// Producers publish them only once the save of the activity committed (rule 5); the envelope's actor is the character.
// JobId is the JobDef id (blacksmith, jeweler), "" when the activity has no job. Tier is the tier or difficulty (1 and up
// when it pays job XP). OpKey is the operation's idempotency key: consumers drop a repeated key (a repeated Harvested adds
// nothing). Consumed by JobService (job XP), AchievementService (gathering and crafting counters) and, later, Demand,
// Portfolio, Quests and Honor (spec 8.1).

/// <summary>
/// What every job activity event carries, so one consumer can weigh them all (<c>JobService</c>). <see cref="Activity"/> is
/// fixed per event type; <see cref="SubjectNetId"/> is the node or recipe that diminishing returns count repeats of.
/// </summary>
public interface IJobActivityEvent : IGameEvent
{
    long CharacterId { get; }

    string JobId { get; }

    JobActivity Activity { get; }

    int Tier { get; }

    int SubjectNetId { get; }

    string OpKey { get; }
}

/// <summary>
/// A resource node was harvested (R-5.2-01..03; WP-M3-5 publishes it): <see cref="Count"/> units of <see cref="ItemNetId"/>
/// from node type <see cref="NodeDefNetId"/>. Demand counts it per node type and region (R-4.2-03, R-5.2-03). The record
/// carries no region: the publisher sets the node's region index as the envelope's <see cref="EventEnvelope.RegionId"/>
/// (<c>Publish(in e, actorCharacterId: characterId, regionId: node.RegionIndex)</c>), which <c>DemandService</c> reads.
/// </summary>
public readonly record struct Harvested(long CharacterId, uint EntityId, string JobId, int Tier, int NodeDefNetId, int ItemNetId,
    int Count, string OpKey) : IJobActivityEvent
{
    public JobActivity Activity => JobActivity.Harvest;

    public int SubjectNetId => NodeDefNetId;
}

/// <summary>A refine recipe ran (R-5.3-06: gems, cloth): <see cref="Count"/> units of <see cref="OutputItemNetId"/>.</summary>
public readonly record struct Refined(long CharacterId, uint EntityId, string JobId, int Tier, int RecipeNetId, int OutputItemNetId,
    int Count, string OpKey) : IJobActivityEvent
{
    public JobActivity Activity => JobActivity.Refine;

    public int SubjectNetId => RecipeNetId;
}

/// <summary>
/// A forge session finished (R-5.3-01..03; WP-M3-6): one <see cref="OutputItemNetId"/> of <see cref="Rarity"/> and
/// <see cref="Quality"/>. The crafter is <see cref="CharacterId"/>; <see cref="RecipientCharacterId"/> got the item (the same
/// character in M3; the contract customer in M4).
/// </summary>
public readonly record struct Crafted(long CharacterId, uint EntityId, string JobId, int Tier, int RecipeNetId, int OutputItemNetId,
    byte Rarity, int Quality, long RecipientCharacterId, string OpKey) : IJobActivityEvent
{
    public JobActivity Activity => JobActivity.Craft;

    public int SubjectNetId => RecipeNetId;
}

/// <summary>A tier conversion was rolled (R-5.2-04): <see cref="Tier"/> is the target tier; a failure consumed the inputs.</summary>
public readonly record struct Converted(long CharacterId, uint EntityId, string JobId, int Tier, int RecipeNetId, bool Success,
    string OpKey) : IJobActivityEvent
{
    public JobActivity Activity => JobActivity.Convert;

    public int SubjectNetId => RecipeNetId;
}

/// <summary>An enchant attempt (R-5.3-04): the item went from <see cref="FromLevel"/> to <see cref="ToLevel"/>; <see cref="Tier"/> is the level before.</summary>
public readonly record struct Enchanted(long CharacterId, uint EntityId, string JobId, int Tier, int ItemNetId, long InstanceId,
    int FromLevel, int ToLevel, string OpKey) : IJobActivityEvent
{
    public JobActivity Activity => JobActivity.Enchant;

    public int SubjectNetId => ItemNetId;
}

/// <summary>A socket attempt (R-5.3-05): <see cref="Tier"/> is the gem's tier; a failure consumed the gem.</summary>
public readonly record struct GemSocketed(long CharacterId, uint EntityId, string JobId, int Tier, int GemItemNetId, long InstanceId,
    bool Success, string OpKey) : IJobActivityEvent
{
    public JobActivity Activity => JobActivity.GemSocket;

    public int SubjectNetId => GemItemNetId;
}

/// <summary>A gem was taken out of an item by a Jeweler (R-5.3-05).</summary>
public readonly record struct GemRemoved(long CharacterId, uint EntityId, string JobId, int Tier, int GemItemNetId, long InstanceId,
    string OpKey) : IJobActivityEvent
{
    public JobActivity Activity => JobActivity.GemRemove;

    public int SubjectNetId => GemItemNetId;
}

/// <summary>
/// A consumable was used (R-3.4-03; WP-M3-3 publishes it): <see cref="Count"/> units of <see cref="ItemNetId"/>. No job XP in
/// M3 (no <c>JobXpWeights</c> row): <see cref="JobId"/> is "" and <see cref="Tier"/> 0.
/// </summary>
public readonly record struct ItemUsed(long CharacterId, uint EntityId, string JobId, int Tier, int ItemNetId, int Count, string OpKey)
    : IJobActivityEvent
{
    public JobActivity Activity => JobActivity.ItemUse;

    public int SubjectNetId => ItemNetId;
}

/// <summary>A job pick committed (spec 5.1 M3 decisions: permanent); published by the world after the save (rule 5).</summary>
public readonly record struct JobChosen(long CharacterId, uint EntityId, string JobId, JobSlot Slot) : IGameEvent;

/// <summary>
/// A job went from <see cref="FromLevel"/> to <see cref="ToLevel"/> (several levels of one gain are one event) and gained
/// <see cref="PointsGained"/> job points. Class 2 like character level-ups: published when the XP is applied.
/// </summary>
public readonly record struct JobLevelUp(long CharacterId, uint EntityId, string JobId, int FromLevel, int ToLevel, int PointsGained)
    : IGameEvent;

/// <summary>
/// An activity paid job XP (telemetry and the bot report): <see cref="Amount"/> after the tier factor and the diminishing
/// return of <see cref="Repeats"/> earlier repeats (0 is possible at the job level cap).
/// </summary>
public readonly record struct JobXpGained(long CharacterId, uint EntityId, string JobId, JobActivity Activity, int Amount, int Repeats,
    string OpKey) : IGameEvent;
