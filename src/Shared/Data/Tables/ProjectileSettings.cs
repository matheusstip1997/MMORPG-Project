namespace Shared.Data.Tables
{
    /// <summary>Limits of the projectile system (spec 3.2: pooled, capped per owner). Placeholders in data/tables.</summary>
    [Table("ProjectileSettings", 1)]
    [Singleton]
    public sealed class ProjectileSettings
    {
        /// <summary>Live projectiles one owner may have; a spawn over the cap is refused (and counted).</summary>
        [Range(1, 256)] public int MaxPerOwner { get; set; }

        /// <summary>Pool size: live projectiles in one world. A spawn with the pool empty is refused (and counted).</summary>
        [Range(1, 65_536)] public int MaxActive { get; set; }

        /// <summary>
        /// Ticks in a row each spawn and despawn event is sent to a player (1 = once). Projectile events go on the unreliable
        /// channel, so repeating them survives packet loss, like the input redundancy of spec 1.7; clients drop duplicates.
        /// </summary>
        [Range(1, 10)] public int EventRepeatTicks { get; set; }
    }
}
