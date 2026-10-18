namespace Shared.Data.Tables
{
    /// <summary>What an effect does (R-3.1-07, spec 3.1 implementation). Config enum (camelCase names); append only.</summary>
    public enum EffectKind : byte
    {
        /// <summary>A hit with poise damage and flinch (R-3.1-08, R-3.3-07). Resolved by HitResolver: TODO(D3).</summary>
        Strike = 1,
        /// <summary>Spawns a projectile (spec 3.2). TODO(D3): ProjectileDef reference.</summary>
        Projectile = 2,
        /// <summary>TODO(D6): heal amount (formula from spec 3.4/3.7).</summary>
        Heal = 3,
        /// <summary>Applies a status (spec 3.7). TODO(D6).</summary>
        ApplyStatus = 4,
        /// <summary>Removes a status (spec 3.7). TODO(D6).</summary>
        RemoveStatus = 5,
        /// <summary>Moves the caster along the facing locked at action start, with world collision (done in D2).</summary>
        Dash = 6,
        /// <summary>An area on the ground with a duration and a tick rate; never flinches (R-3.3-09). TODO(D3, D6).</summary>
        GroundArea = 7,
    }

    /// <summary>
    /// One effect of an action (spec 3.1 <c>ActionEffectDef</c>, R-3.1-07): pinned to a phase of the action and an offset
    /// inside it. It fires on the tick its phase reaches the offset (rounded up to whole ticks, never past the phase);
    /// effects that have not fired when the action is cancelled or interrupted are dropped. Config rows hold no nested
    /// objects, so the parameters of every kind are flat fields here; a field the kind does not use must be 0 or empty.
    /// <list type="bullet">
    /// <item>Strike: <see cref="PoiseDamage"/>, <see cref="FlinchPower"/>, <see cref="FlinchMs"/> (all required). TODO(D3): hit shapes.</item>
    /// <item>Dash: <see cref="DistanceCm"/> over <see cref="DurationMs"/>, inside the action.</item>
    /// <item>GroundArea: <see cref="DurationMs"/>, <see cref="TickIntervalMs"/>, <see cref="PoiseDamage"/> per tick. TODO(D3): shape; TODO(D6): damage.</item>
    /// <item>ApplyStatus, RemoveStatus: <see cref="StatusId"/>. TODO(D6): a [Ref] to StatusDef once it exists.</item>
    /// <item>Projectile, Heal: no parameters yet (TODO(D3), TODO(D6)).</item>
    /// </list>
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

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
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
                case EffectKind.Heal:
                    Unused(errors, distance: true, duration: true, tickInterval: true, poise: true, flinch: true, status: true);
                    break;
                default:
                    errors.Add(nameof(Kind), "unknown effect kind");
                    break;
            }
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
