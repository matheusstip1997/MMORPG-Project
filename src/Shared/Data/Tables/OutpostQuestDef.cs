using Shared.Protocol;

namespace Shared.Data.Tables
{
    /// <summary>
    /// A kind of server-generated outpost activity (R-5.5-01, spec 5.5; WP-M2-6). Config enum (camelCase names) and on the wire
    /// in <c>QuestBoard</c> and <c>RunStatus</c>: append only, never renumber.
    /// </summary>
    public enum QuestKind : byte
    {
        None = 0,
        /// <summary>Kill X monsters of one type (progress from <c>Killed</c>).</summary>
        Kill = 1,
        /// <summary>Deliver X units of a drop of the zone's monsters at the outpost (consumed: an item sink, R-5.5-02).</summary>
        Deliver = 2,
        /// <summary>
        /// Return X quest items: while the step is active the named monster type drops one into the Quest bag per kill (100 %),
        /// delivered at the outpost like <see cref="Deliver"/>.
        /// </summary>
        ReturnItem = 3,
    }

    /// <summary>
    /// One activity kind the quest board generator may offer (R-5.5-01; spec 5.5 "M2 parameters", user decision 2026-10-27).
    /// Each board slot picks a kind by <see cref="Weight"/>, then a monster of the outpost's zone weighted by its demand reward
    /// percent (R-4.2-03). The count is drawn evenly in <see cref="CountMin"/>..<see cref="CountMax"/> and, when
    /// <see cref="DemandScaled"/>, scaled by the reward percent and clamped to that range. The locked reward of a step is the
    /// monster's base exp x <see cref="ExpPercentPerUnit"/> x count x the reward percent (<c>RunMath.StepReward</c>).
    /// One row per kind. Values in data/tables/OutpostQuestDef.json are placeholders until the balance phase.
    /// </summary>
    [Table("OutpostQuestDef", 1)]
    public sealed class OutpostQuestDef : IValidatableRow, ISetValidatableRow
    {
        [TableId] public string Id { get; set; } = string.Empty;

        public QuestKind Kind { get; set; }

        /// <summary>Relative chance of this kind per board slot; 0 never offers it.</summary>
        [Range(0, 1_000_000)] public int Weight { get; set; }

        [Range(1, QuestMessages.MaxCount)] public int CountMin { get; set; }

        [Range(1, QuestMessages.MaxCount)] public int CountMax { get; set; }

        /// <summary>Exp per unit of the count, in percent of the monster's base exp (<c>ProgressionCurves.BaseExpOf</c>).</summary>
        [Range(0, 100_000)] public int ExpPercentPerUnit { get; set; }

        /// <summary>The count follows the demand reward percent (kill X, "5 to 20, demand-scaled").</summary>
        public bool DemandScaled { get; set; }

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
            if (Kind == QuestKind.None || Kind > QuestKind.ReturnItem)
                errors.Add(nameof(Kind), "must be kill, deliver or returnItem");
            if (CountMax < CountMin)
                errors.Add(nameof(CountMax), "must be at least CountMin");
        }

        /// <summary>
        /// One row per kind; some kind can be offered (a weight above 0); a returnItem count fits one stack of every quest item
        /// a monster drops (<see cref="QuestItemDrop"/>), so the Quest bag never splits it.
        /// </summary>
        public void Validate(ConfigSet set, RowErrors errors)
        {
            var rows = NetIds.Rows<OutpostQuestDef>(set);
            bool anyWeight = false;
            foreach (var other in rows)
            {
                anyWeight |= other.Weight > 0;
                if (!ReferenceEquals(other, this) && other.Kind == Kind && rows.IndexOf(other) < rows.IndexOf(this))
                    errors.Add(nameof(Kind), $"kind {Kind} already has row '{other.Id}'");
            }
            if (!anyWeight && ReferenceEquals(rows[0], this))
                errors.Add(nameof(Weight), "no kind has a weight above 0: the board would be empty");
            if (Kind != QuestKind.ReturnItem)
                return;
            foreach (var drop in NetIds.Rows<QuestItemDrop>(set))
            {
                var item = ItemDef.Find(set, drop.ItemId);
                if (item != null && item.MaxStack < CountMax)
                    errors.Add(nameof(CountMax), $"{CountMax} quest items do not fit one stack of '{item.Id}' (maxStack {item.MaxStack})");
            }
        }
    }
}
