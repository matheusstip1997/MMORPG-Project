namespace Shared.Data.Tables
{
    /// <summary>
    /// One kind of projectile (spec 3.2, R-3.2-01), named by the <c>projectileId</c> of a <see cref="EffectKind.Projectile"/>
    /// effect. It flies straight along the facing locked at the start of its action from the caster's position, at
    /// <see cref="SpeedCmPerSec"/>, until it has flown <see cref="RangeCm"/> (its lifetime is range / speed), hit
    /// <see cref="MaxHits"/> targets, or met a wall (<see cref="StopsOnWorld"/>). Targets are circles; the projectile is a
    /// circle of <see cref="HitRadiusCm"/> swept along each tick's segment (<c>SweptCircle</c>). Every hit goes through the
    /// normal hit rules (R-3.2-02), with the poise and flinch of the effect row; hit k (0 for the first) deals
    /// <c>100 - k * falloffPercentPerHit</c> percent of the damage. Path: <c>Shared.Simulation.Combat.ProjectilePath</c>.
    /// </summary>
    [Table("ProjectileDef", 1)]
    public sealed class ProjectileDef : IValidatableRow, ISetValidatableRow
    {
        /// <summary>Highest <see cref="NetId"/>: spawn messages send it in 12 bits (<c>ProjectileMessages.DefIdBits</c>).</summary>
        public const int MaxNetId = 4095;

        /// <summary>Highest speed: spawn messages send it in 17 bits; the per-tick step is checked against the sweep limit too.</summary>
        public const int MaxSpeedCmPerSec = 100_000;

        /// <summary>
        /// Longest step per tick a projectile may take: the per-axis segment limit of <c>SweptCircle.MaxRaw</c> (4096 cm).
        /// </summary>
        public const int MaxStepCmPerTick = 4096;

        [TableId] public string Id { get; set; } = string.Empty;

        /// <summary>Its id on the wire (spec 3.2 spawn event "type"); unique, 1 to <see cref="MaxNetId"/>.</summary>
        [Range(1, MaxNetId)] public int NetId { get; set; }

        [Range(1, MaxSpeedCmPerSec)] public int SpeedCmPerSec { get; set; }

        /// <summary>How far it flies before it despawns.</summary>
        [Range(1, 100_000)] public int RangeCm { get; set; }

        /// <summary>The projectile's own radius, added to each target's radius for the sweep.</summary>
        [Range(1, 2_000)] public int HitRadiusCm { get; set; }

        /// <summary>Targets it hits before it despawns: 1 stops at the first target, more pierce (R-3.2-01).</summary>
        [Range(1, 32)] public int MaxHits { get; set; }

        /// <summary>Whether a wall of the world grid stops it.</summary>
        public bool StopsOnWorld { get; set; }

        /// <summary>Damage lost per earlier hit, in percent of the full damage (optional, spec 3.2); 0 keeps it whole.</summary>
        [Range(0, 100)] public int FalloffPercentPerHit { get; set; }

        /// <summary>How the client draws it (an <see cref="EntityVisualDef"/> row).</summary>
        [Ref(typeof(EntityVisualDef))] public string VisualId { get; set; } = string.Empty;

        /// <summary>
        /// WP-M2-9: the burst the client draws where the projectile explodes (a hit, a wall or the end of its range) when the
        /// effect that fired it has an <c>impactEffectId</c>; an <see cref="EntityVisualDef"/> row, client only. Empty: none.
        /// </summary>
        [Optional, Ref(typeof(EntityVisualDef), Optional = true)] public string ImpactVisualId { get; set; } = string.Empty;

        /// <summary>The DamagePercent of hit <paramref name="hitIndex"/> (0 for the first).</summary>
        public int DamagePercentOfHit(int hitIndex) => 100 - hitIndex * FalloffPercentPerHit;

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
            // Every hit up to maxHits must still deal damage.
            if (MaxHits > 0 && DamagePercentOfHit(MaxHits - 1) <= 0)
                errors.Add(nameof(FalloffPercentPerHit), $"the last of {MaxHits} hits would deal no damage");
        }

        public void Validate(ConfigSet set, RowErrors errors)
        {
            NetIds.CheckUnique<ProjectileDef>(set, this, NetId, row => row.NetId, row => row.Id, errors);
            if (!set.TryGet(typeof(StartingValuesM1), out var values) || values.Rows.Count != 1)
                return;
            int tickRateHz = ((StartingValuesM1)values.Rows[0]).TickRateHz;
            if (tickRateHz > 0 && (SpeedCmPerSec + tickRateHz - 1) / tickRateHz > MaxStepCmPerTick)
                errors.Add(nameof(SpeedCmPerSec), $"moves more than {MaxStepCmPerTick} cm per tick at {tickRateHz} Hz");
        }

        public static ProjectileDef? Find(ConfigSet set, string id) => NetIds.Find<ProjectileDef>(set, id, row => row.Id);
    }
}
