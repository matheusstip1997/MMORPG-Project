namespace Shared.Data.Tables
{
    /// <summary>
    /// Main HUD settings (spec 9.7, backlog I). Client-only. Holds the root fields of the layout schema
    /// (<c>referenceResolution</c>, <c>scaling</c>), which the table format cannot nest inside <see cref="HudLayout"/>, and
    /// the timings and pool sizes of the HUD widgets and world overlays. Every value is a display choice, not a game rule.
    /// </summary>
    [Table("HudSettings", 1)]
    [Singleton]
    public sealed class HudSettings
    {
        /// <summary>Spec 9.2: 1280 x 720.</summary>
        [Range(320, 7_680)] public int ReferenceWidth { get; set; }
        [Range(240, 4_320)] public int ReferenceHeight { get; set; }

        public UiScaleMode Scaling { get; set; }

        /// <summary>How long the refusal or parry result toast stays (spec 9.4 <c>ActionResult</c>).</summary>
        [Range(300, 10_000)] public int ToastMs { get; set; }

        /// <summary>How long a hotbar slot flashes after its press was refused (spec 9.9 hotbar flow).</summary>
        [Range(100, 5_000)] public int SlotFlashMs { get; set; }

        /// <summary>Damage numbers drawn at once (spec 9.9: pooled); a new hit past the pool reuses the oldest number.</summary>
        [Range(4, 256)] public int DamageNumberPool { get; set; }

        [Range(200, 5_000)] public int DamageNumberLifetimeMs { get; set; }

        /// <summary>Height above the target's feet where a damage number starts.</summary>
        [Range(0, 1_000)] public int DamageNumberHeightCm { get; set; }

        /// <summary>How far a damage number rises over its lifetime.</summary>
        [Range(0, 1_000)] public int DamageNumberRiseCm { get; set; }

        /// <summary>Name plates drawn at once (spec 9.9: pooled); entities past the pool get none.</summary>
        [Range(4, 512)] public int NamePlatePool { get; set; }

        /// <summary>Height above an entity's feet where its name plate sits.</summary>
        [Range(0, 1_000)] public int NamePlateHeightCm { get; set; }

        /// <summary>Height of a normal monster's HP bar (spec 9.3 <c>monster_hp_bar</c>: below the monster); 0 is its feet.</summary>
        [Range(-500, 1_000)] public int MonsterHpBarHeightCm { get; set; }

        /// <summary>
        /// Boss bar engagement (spec 9.3 <c>boss_bar</c>; user decision 2026-10-24): the bar shows while the player hit the
        /// boss, or the boss hit the player, within this time.
        /// </summary>
        [Range(1_000, 120_000)] public int BossEngageTimeoutMs { get; set; }

        /// <summary>Ground telegraphs are drawn this far above the attacker's feet so they do not flicker into the ground.</summary>
        [Range(0, 50)] public int TelegraphGroundOffsetCm { get; set; }

        /// <summary>Height of the telegraph's color icon above the attacker (spec 9.8: color plus shape).</summary>
        [Range(0, 1_500)] public int TelegraphIconHeightCm { get; set; }

        /// <summary>The P square flashes this long in <c>HudThemeTokens.parryFlash</c> on a <c>ParrySuccess</c> (spec 9.3 parry_dodge).</summary>
        [Optional, Range(0, 2_000)] public int ParryFlashMs { get; set; } = 250;

        /// <summary>A monster whose hit the own player parried flashes this long in <c>HudThemeTokens.hitFlash</c> (spec 9.3).</summary>
        [Optional, Range(0, 2_000)] public int HitFlashMs { get; set; } = 120;

        /// <summary>
        /// How bright the flash is: the body's base color becomes <c>hitFlash</c> times this percent (lit materials multiply
        /// it with their texture, so above 100 is needed to read as a flash).
        /// </summary>
        [Optional, Range(100, 1_000)] public int HitFlashBoostPercent { get; set; } = 300;
    }
}
