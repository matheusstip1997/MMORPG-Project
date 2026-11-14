using Shared.World;

namespace Shared.Data.Tables
{
    /// <summary>
    /// The owner-priority window of a death loot bag per area type (spec 4.3 R-4.3-03, 8.5; M4 decision 8; WP-M4-9): for this
    /// long after the death only the owner takes, and members of the owner's party collect for the owner (by mail, no stolen
    /// mark); everyone else is refused <c>BagLocked</c>. One row per <see cref="RegionDefaults"/> area type whose
    /// <c>deathDrop</c> is <c>on</c>. Every number is a placeholder (balance is deferred).
    /// </summary>
    [Table("OwnerWindows", 1)]
    public sealed class OwnerWindows : IValidatableRow, ISetValidatableRow
    {
        /// <summary>The area type (a <see cref="RegionDefaults"/> id).</summary>
        [TableId, Ref(typeof(RegionDefaults))] public string Id { get; set; } = string.Empty;

        /// <summary>Milliseconds after the death during which the bag is the owner's (and their party's).</summary>
        [Range(0, 3_600_000)] public int OwnerWindowMs { get; set; }

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
        }

        /// <summary>
        /// The row's area type drops a bag (<c>deathDrop</c> on), every area type that does has a row, and the window ends before
        /// the bag expires (<see cref="BagExpiry.ExpiryMs"/>).
        /// </summary>
        public void Validate(ConfigSet set, RowErrors errors)
        {
            var rows = NetIds.Rows<OwnerWindows>(set);
            var areas = NetIds.Rows<RegionDefaults>(set);
            var area = areas.Find(a => a.Id == Id);
            if (area != null && area.DeathDrop != DeathDrop.On)
                errors.Add(nameof(Id), $"area type {Id} drops no loot bag (deathDrop {area.DeathDrop}): no owner window");
            if (rows.IndexOf(this) == 0)
            {
                foreach (var drops in areas)
                {
                    if (drops.DeathDrop == DeathDrop.On && rows.Find(r => r.Id == drops.Id) == null)
                        errors.Add(nameof(Id), $"area type {drops.Id} drops a loot bag (deathDrop on) but has no owner window row");
                }
            }
            if (set.TryGet(typeof(BagExpiry), out var expiry) && expiry.Rows.Count == 1
                && OwnerWindowMs >= ((BagExpiry)expiry.Rows[0]).ExpiryMs)
                errors.Add(nameof(OwnerWindowMs), $"{OwnerWindowMs} ms must end before the bag expires (BagExpiry.expiryMs)");
        }
    }
}
