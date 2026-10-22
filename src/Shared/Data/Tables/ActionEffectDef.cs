using Shared.Simulation.Stats;

namespace Shared.Data.Tables
{
    /// <summary>What an effect does (R-3.1-07, spec 3.1 implementation). Config enum (camelCase names); append only.</summary>
    public enum EffectKind : byte
    {
        /// <summary>A hit with poise damage and flinch (R-3.1-08, R-3.3-07), from its <see cref="HitShapeDef"/> rows (R-3.1-03).</summary>
        Strike = 1,
        /// <summary>Spawns the <see cref="ProjectileDef"/> its <c>projectileId</c> names (spec 3.2); every hit carries the effect's poise and flinch.</summary>
        Projectile = 2,
        /// <summary>TODO(D6): heal amount (formula from spec 3.4/3.7).</summary>
        Heal = 3,
        /// <summary>Applies a status (spec 3.7). TODO(D6).</summary>
        ApplyStatus = 4,
        /// <summary>Removes a status (spec 3.7). TODO(D6).</summary>
        RemoveStatus = 5,
        /// <summary>Moves the caster along the facing locked at action start, with world collision (done in D2).</summary>
        Dash = 6,
        /// <summary>An area on the ground (its <see cref="HitShapeDef"/> rows) with a duration and a tick rate; never flinches (R-3.3-09). TODO(D6).</summary>
        GroundArea = 7,
    }

    /// <summary>
    /// Which attack stat a damaging effect uses (R-3.4-04 step 1: Atk or Matk, with Def or Magic def and the matching
    /// penetration). Config enum (camelCase names); append only. <see cref="None"/> for effects that deal no damage.
    /// </summary>
    public enum EffectDamageKind : byte
    {
        None = 0,
        Physical = 1,
        Magic = 2,
    }

    /// <summary>
    /// One effect of an action (spec 3.1 <c>ActionEffectDef</c>, R-3.1-07): pinned to a phase of the action and an offset
    /// inside it. It fires on the tick its phase reaches the offset (rounded up to whole ticks, never past the phase);
    /// effects that have not fired when the action is cancelled or interrupted are dropped. Config rows hold no nested
    /// objects, so the parameters of every kind are flat fields here; a field the kind does not use must be 0 or empty.
    /// <list type="bullet">
    /// <item>Strike: <see cref="PoiseDamage"/>, <see cref="FlinchPower"/>, <see cref="FlinchMs"/>, <see cref="DamagePercent"/>, <see cref="DamageKind"/> (all required); at least one <see cref="HitShapeDef"/> row (R-3.1-03).</item>
    /// <item>Dash: <see cref="DistanceCm"/> over <see cref="DurationMs"/>, inside the action.</item>
    /// <item>GroundArea: <see cref="DurationMs"/>, <see cref="TickIntervalMs"/>, <see cref="PoiseDamage"/> per tick; at least one <see cref="HitShapeDef"/> row. TODO(D6): damage.</item>
    /// <item>ApplyStatus, RemoveStatus: <see cref="StatusId"/>. TODO(D6): a [Ref] to StatusDef once it exists.</item>
    /// <item>Projectile: <see cref="ProjectileId"/>, and the <see cref="PoiseDamage"/>, <see cref="FlinchPower"/>, <see cref="FlinchMs"/>, <see cref="DamagePercent"/>, <see cref="DamageKind"/> of each of its hits (R-3.1-08, R-3.2-02, all required).</item>
    /// <item>Heal: no parameters yet (TODO(D6)).</item>
    /// </list>
    /// TODO(D6): the element of a damaging effect (R-3.4-04 step 5); every hit is non-elemental until then. TODO: the flat
    /// damage of step 1.
    /// </summary>
    [Table("ActionEffectDef", 1)]
    public sealed class ActionEffectDef : IValidatableRow, ISetValidatableRow
    {
        /// <summary>Effects per action the ActionMachine schedules (it tracks fired effects in one byte).</summary>
        public const int MaxEffectsPerAction = 8;

        [TableId] public string Id { get; set; } = string.Empty;

        [Ref(typeof(ActionDef))] public string ActionId { get; set; } = string.Empty;

        public EffectKind Kind { get; set; }

        public ActionPhase Phase { get; set; }

        /// <summary>From the start of <see cref="Phase"/>; must be shorter than the phase.</summary>
        [Range(0, 60_000)] public int OffsetMs { get; set; }

        [Range(0, 5_000)] public int DistanceCm { get; set; }
        [Range(0, 600_000)] public int DurationMs { get; set; }
        [Range(0, 60_000)] public int TickIntervalMs { get; set; }
        [Range(0, 10_000)] public int PoiseDamage { get; set; }
        [Range(0, 10_000)] public int FlinchPower { get; set; }
        [Range(0, 10_000)] public int FlinchMs { get; set; }
        public string StatusId { get; set; } = string.Empty;

        /// <summary>
        /// R-3.4-04 step 1, the skill multiplier in whole percents (100 = x1) on the attacker's Atk or Matk. Strike and
        /// Projectile only (bounded by the damage formula's overflow limit).
        /// </summary>
        [Range(0, DamageCalc.MaxSkillMultiplierPercent)] public int DamagePercent { get; set; }

        /// <summary>R-3.4-04 step 1: Atk or Matk. Strike and Projectile only.</summary>
        public EffectDamageKind DamageKind { get; set; }

        /// <summary>True for the kinds that deal damage through the hit pipeline of spec 3.1.</summary>
        public static bool DealsDamage(EffectKind kind) => kind == EffectKind.Strike || kind == EffectKind.Projectile;

        /// <summary>The projectile a <see cref="EffectKind.Projectile"/> spawns (spec 3.2); empty for every other kind.</summary>
        [Ref(typeof(ProjectileDef), Optional = true)] public string ProjectileId { get; set; } = string.Empty;

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
            if (DealsDamage(Kind))
            {
                Require(errors, nameof(DamagePercent), DamagePercent, $"a {Kind} needs a damage multiplier (R-3.4-04)");
                if (DamageKind != EffectDamageKind.Physical && DamageKind != EffectDamageKind.Magic)
                    errors.Add(nameof(DamageKind), $"a {Kind} deals physical or magic damage (R-3.4-04)");
            }
            else
            {
                // TODO(D6): ground areas deal damage per tick (spec 3.1); until then no other kind has damage fields.
                if (DamagePercent != 0) errors.Add(nameof(DamagePercent), $"must be 0 for {Kind}");
                if (DamageKind != EffectDamageKind.None) errors.Add(nameof(DamageKind), $"must be none for {Kind}");
            }
            switch (Kind)
            {
                case EffectKind.Strike:
                    // R-3.1-08: every hit carries poise damage; R-3.3-07: and a flinch power and a base flinch time.
                    Require(errors, nameof(PoiseDamage), PoiseDamage, "every hit carries poise damage (R-3.1-08)");
                    Require(errors, nameof(FlinchPower), FlinchPower, "every hit carries a flinch power (R-3.3-07)");
                    Require(errors, nameof(FlinchMs), FlinchMs, "every hit carries a base flinch time (R-3.3-07)");
                    Unused(errors, distance: true, duration: true, tickInterval: true, status: true);
                    break;
                case EffectKind.Dash:
                    Require(errors, nameof(DistanceCm), DistanceCm, "a dash must move");
                    Require(errors, nameof(DurationMs), DurationMs, "a dash must take time");
                    Unused(errors, tickInterval: true, poise: true, flinch: true, status: true);
                    break;
                case EffectKind.GroundArea:
                    Require(errors, nameof(DurationMs), DurationMs, "a ground area must last");
                    Require(errors, nameof(TickIntervalMs), TickIntervalMs, "a ground area needs a tick rate");
                    if (TickIntervalMs > DurationMs)
                        errors.Add(nameof(TickIntervalMs), "must not be longer than the area lasts");
                    if (FlinchPower != 0 || FlinchMs != 0)
                        errors.Add(nameof(FlinchPower), "ground effects never flinch (R-3.3-09)");
                    Unused(errors, distance: true, status: true);
                    break;
                case EffectKind.ApplyStatus:
                case EffectKind.RemoveStatus:
                    if (string.IsNullOrWhiteSpace(StatusId))
                        errors.Add(nameof(StatusId), "must name a status");
                    Unused(errors, distance: true, duration: true, tickInterval: true, poise: true, flinch: true);
                    break;
                case EffectKind.Projectile:
                    // R-3.1-08 / R-3.2-02: a projectile hits through the normal rules, so each hit carries poise and flinch.
                    if (string.IsNullOrWhiteSpace(ProjectileId))
                        errors.Add(nameof(ProjectileId), "a projectile effect must name a ProjectileDef");
                    Require(errors, nameof(PoiseDamage), PoiseDamage, "every hit carries poise damage (R-3.1-08)");
                    Require(errors, nameof(FlinchPower), FlinchPower, "every hit carries a flinch power (R-3.3-07)");
                    Require(errors, nameof(FlinchMs), FlinchMs, "every hit carries a base flinch time (R-3.3-07)");
                    Unused(errors, distance: true, duration: true, tickInterval: true, status: true);
                    break;
                case EffectKind.Heal:
                    Unused(errors, distance: true, duration: true, tickInterval: true, poise: true, flinch: true, status: true);
                    break;
                default:
                    errors.Add(nameof(Kind), "unknown effect kind");
                    break;
            }
            if (Kind != EffectKind.Projectile && ProjectileId.Length != 0)
                errors.Add(nameof(ProjectileId), $"must be empty for {Kind}");
        }

        public void Validate(ConfigSet set, RowErrors errors)
        {
            var action = ActionDef.Find(set, ActionId);
            if (action == null)
                return; // reported as BrokenRef
            int phaseMs = action.PhaseMs(Phase);
            if (phaseMs == 0)
                errors.Add(nameof(Phase), $"'{ActionId}' has no {Phase} phase");
            else if (OffsetMs >= phaseMs)
                errors.Add(nameof(OffsetMs), $"must be shorter than {Phase} of '{ActionId}' ({phaseMs} ms)");
            if (Kind == EffectKind.Dash && action.PhaseStartMs(Phase) + OffsetMs + DurationMs > action.TotalMs())
                errors.Add(nameof(DurationMs), $"the dash must end before '{ActionId}' ends");
            // R-3.1-03: strikes (and ground areas, spec 3.1) are resolved from shapes. CountFor is -1 when HitShapeDef is not loaded.
            if (HitShapeDef.TakesShapes(Kind) && HitShapeDef.CountFor(set, Id) == 0)
                errors.Add(nameof(Id), $"a {Kind} needs at least one HitShapeDef row");

            if (!set.TryGet(typeof(ActionEffectDef), out var table))
                return;
            int count = 0;
            foreach (var row in table.Rows)
            {
                if (((ActionEffectDef)row).ActionId == ActionId)
                    count++;
            }
            if (count > MaxEffectsPerAction)
                errors.Add(nameof(ActionId), $"'{ActionId}' has {count} effects, at most {MaxEffectsPerAction}");
        }

        private static void Require(RowErrors errors, string field, int value, string message)
        {
            if (value <= 0)
                errors.Add(field, message);
        }

        private void Unused(RowErrors errors, bool distance = false, bool duration = false, bool tickInterval = false,
            bool poise = false, bool flinch = false, bool status = false)
        {
            string kind = Kind.ToString();
            if (distance && DistanceCm != 0) errors.Add(nameof(DistanceCm), $"must be 0 for {kind}");
            if (duration && DurationMs != 0) errors.Add(nameof(DurationMs), $"must be 0 for {kind}");
            if (tickInterval && TickIntervalMs != 0) errors.Add(nameof(TickIntervalMs), $"must be 0 for {kind}");
            if (poise && PoiseDamage != 0) errors.Add(nameof(PoiseDamage), $"must be 0 for {kind}");
            if (flinch && (FlinchPower != 0 || FlinchMs != 0)) errors.Add(nameof(FlinchPower), $"must be 0 for {kind}");
            if (status && StatusId.Length != 0) errors.Add(nameof(StatusId), $"must be empty for {kind}");
        }
    }
}
