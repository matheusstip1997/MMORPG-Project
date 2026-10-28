namespace Shared.Data.Tables
{
    /// <summary>
    /// Limits of the status system (spec 3.7 "M2 status list", user decision 2026-10-27; placeholders). The stack caps are
    /// <see cref="StatusEffectDef.MaxStacks"/> limits checked by the validator (user answer 2026-10-28): a control status
    /// (<see cref="StatusControl"/>) stacks at most <see cref="ControlMaxStacks"/>, a debuff that changes stats at most
    /// <see cref="StatDebuffMaxStacks"/>. <see cref="BuffRowMaxIcons"/> is how many icons the HUD buff row shows (spec 9.7);
    /// more statuses stay active but are not shown (the snapshot header's 15 stays the wire cap).
    /// </summary>
    [Table("StatusSettings", 1)]
    [Singleton]
    public sealed class StatusSettings
    {
        [Range(1, StatusEffectDef.MaxStackLimit)] public int ControlMaxStacks { get; set; }

        [Range(1, StatusEffectDef.MaxStackLimit)] public int StatDebuffMaxStacks { get; set; }

        [Range(1, StatusEffectDef.MaxStackLimit)] public int BuffRowMaxIcons { get; set; }

        public static StatusSettings? Find(ConfigSet set) =>
            set.TryGet(typeof(StatusSettings), out var table) && table.Rows.Count == 1 ? (StatusSettings)table.Rows[0] : null;
    }
}
