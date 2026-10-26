namespace Shared.Data.Tables
{
    /// <summary>Limits of the ground area system (spec 3.1 <c>GroundArea</c> effects: pooled, capped per owner). Placeholders in data/tables.</summary>
    [Table("GroundAreaSettings", 1)]
    [Singleton]
    public sealed class GroundAreaSettings
    {
        /// <summary>Live areas one owner may have; a new one over the cap is refused (and counted).</summary>
        [Range(1, 64)] public int MaxPerOwner { get; set; }

        /// <summary>Pool size: live areas in one world. A new one with the pool empty is refused (and counted).</summary>
        [Range(1, 65_536)] public int MaxActive { get; set; }

        /// <summary>
        /// Ticks in a row each spawn and despawn event is sent to a player (1 = once), like <c>ProjectileSettings</c>: the
        /// events go on the unreliable channel, so repeating them survives packet loss; clients drop duplicates.
        /// </summary>
        [Range(1, 10)] public int EventRepeatTicks { get; set; }
    }
}
