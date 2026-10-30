using System;

namespace Shared.Data.Tables
{
    /// <summary>
    /// A job (spec 5.1, R-5.1-01..03; WP-M3-1). M3 has the Blacksmith and the Jeweler (user decision 2026-10-30), both
    /// allowed as main or secondary. A character picks its main job, then its secondary, at a job master NPC from
    /// <see cref="MinLevel"/>; each pick is permanent and grants <see cref="StarterItemIds"/> once (R-5.3-01 starter molds).
    /// Values in data/tables/JobDef.json are placeholders.
    /// </summary>
    [Table("JobDef", 1)]
    public sealed class JobDef : IValidatableRow, ISetValidatableRow
    {
        /// <summary>Largest <see cref="NetId"/>: it travels as one byte in <c>JobState</c> and <c>JobRequest</c>.</summary>
        public const int MaxNetId = byte.MaxValue;

        /// <summary>Stable id, for example <c>blacksmith</c>: the <c>JobId</c> of activity events and <c>job_progress.job_id</c>.</summary>
        [TableId] public string Id { get; set; } = string.Empty;

        /// <summary>Id on the wire; unique, never reused.</summary>
        [Range(1, MaxNetId)] public int NetId { get; set; }

        [Ref(typeof(UiStrings))] public string NameId { get; set; } = string.Empty;

        /// <summary>The job may fill the secondary slot (R-5.1-03: which jobs may be secondary is data).</summary>
        public bool AllowedAsSecondary { get; set; }

        /// <summary>Character level from which the job can be picked (spec 12.3: jobs at levels 12-18).</summary>
        [Range(1, 10_000)] public int MinLevel { get; set; }

        /// <summary>
        /// Items granted once when the job is picked (R-5.3-01 starter molds), one unit each, into the container that receives
        /// their category; the pick is refused when they do not all fit (user decision 2026-10-30).
        /// </summary>
        [Ref(typeof(ItemDef))] public string[] StarterItemIds { get; set; } = Array.Empty<string>();

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
            for (int i = 0; i < StarterItemIds.Length; i++)
            {
                if (Array.IndexOf(StarterItemIds, StarterItemIds[i]) != i)
                    errors.Add(nameof(StarterItemIds), $"lists {StarterItemIds[i]} twice");
            }
        }

        public void Validate(ConfigSet set, RowErrors errors)
        {
            NetIds.CheckUnique<JobDef>(set, this, NetId, row => row.NetId, row => row.Id, errors);
            if (ProgressionSettings.Find(set) is { } progression && MinLevel > progression.MaxLevel)
                errors.Add(nameof(MinLevel), $"{MinLevel} is above ProgressionSettings.maxLevel {progression.MaxLevel}");
            foreach (var id in StarterItemIds)
            {
                if (ItemDef.Find(set, id) is { } item && !ItemCategories.IsInstance(item.Category))
                    errors.Add(nameof(StarterItemIds), $"{id} is a {item.Category} item: starter items are instances (molds, tools)");
            }
        }

        public static JobDef? Find(ConfigSet set, string id) => NetIds.Find<JobDef>(set, id, row => row.Id);
    }
}
