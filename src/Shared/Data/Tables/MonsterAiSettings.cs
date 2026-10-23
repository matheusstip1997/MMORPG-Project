using System;

namespace Shared.Data.Tables
{
    /// <summary>
    /// Settings of the generic monster AI (backlog F; spec 3.1 "M1 implementation, monster AI"), the same for every
    /// monster. Per-monster numbers (attacks, speed, aggro and leash ranges) are on <see cref="MonsterDef"/>. Placeholders
    /// in data/tables.
    /// </summary>
    [Table("MonsterAiSettings", 1)]
    [Singleton]
    public sealed class MonsterAiSettings : IValidatableRow
    {
        /// <summary>How often an idle monster looks for a player to aggro (spread over the ticks by entity id).</summary>
        [Range(1, 10_000)] public int ThinkIntervalMs { get; set; }

        /// <summary>A monster walking home is home this close to its spawn point; there it heals (the leash reset).</summary>
        [Range(1, 10_000)] public int HomeToleranceCm { get; set; }

        /// <summary>
        /// A monster walking home that gets no closer for this long (stuck behind a wall: no pathfinding in M1) is placed
        /// at its spawn point.
        /// </summary>
        [Range(1, 600_000)] public int LeashTimeoutMs { get; set; }

        /// <summary>
        /// Area types monsters never enter (user decision 2026-10-22: the safe city stays safe). Their cells block a monster
        /// like a wall, and a target standing in one is dropped.
        /// </summary>
        [Ref(typeof(RegionDefaults))] public string[] NoEntryAreaTypes { get; set; } = Array.Empty<string>();

        public void Validate(RowErrors errors)
        {
            if (ActionDef.HasDuplicates(NoEntryAreaTypes))
                errors.Add(nameof(NoEntryAreaTypes), "lists an area type twice");
        }
    }
}
