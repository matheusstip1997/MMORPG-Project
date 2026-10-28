namespace Shared.Data.Tables
{
    /// <summary>
    /// The tutorial prompt's timings (spec 12.3, 9.12 <c>tutorial_prompt</c>; WP-M2-10). Client-only, display choices, all
    /// placeholders.
    /// </summary>
    [Table("TutorialSettings", 1)]
    [Singleton]
    public sealed class TutorialSettings
    {
        /// <summary>How long a scheduled prompt stays when it is not dismissed; the next queued prompt follows.</summary>
        [Range(1_000, 120_000)] public int PromptShowMs { get; set; }

        /// <summary>How long the level gate's refusal stays after the player stops pushing into the gate (R-12.3-01).</summary>
        [Range(100, 10_000)] public int GatePromptHoldMs { get; set; }

        public static TutorialSettings? Find(ConfigSet set) =>
            set.TryGet(typeof(TutorialSettings), out var table) && table.Rows.Count == 1 ? (TutorialSettings)table.Rows[0] : null;
    }
}
