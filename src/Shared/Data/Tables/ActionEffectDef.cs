using System;
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
        /// <summary>Restores HP: <c>healPercent</c> of the caster's Matk to each target its <c>targets</c> cover (spec 3.1); never crits.</summary>
        Heal = 3,
        /// <summary>Applies the <see cref="StatusEffectDef"/> its <c>statusId</c> names to its <c>targets</c> (spec 3.7).</summary>
        ApplyStatus = 4,
        /// <summary>Removes one named status, or every dispellable status of a <c>statusKind</c>, from its <c>targets</c> (R-3.7-03).</summary>
        RemoveStatus = 5,
        /// <summary>Moves the caster along the locked facing or the movement keys held at the start (<see cref="DashDirection"/>), with world collision (D2).</summary>
        Dash = 6,
        /// <summary>
        /// An area on the ground (its <see cref="HitShapeDef"/> rows) placed where the caster stands and faces when it fires,
        /// with a duration and a tick rate (spec 3.1 <c>GroundAreaSystem</c>): each tick hits the enemies inside it (damage and
        /// poise, never a flinch: R-3.3-09) and heals the caster or the allies it covers.
        /// </summary>
        GroundArea = 7,

        /// <summary>
        /// A strike placed where a projectile of the same action ends (WP-M2-9, the explosion on impact): fields and
        /// <see cref="HitShapeDef"/> rows as a <see cref="Strike"/>, centred on the end point and turned by the projectile's
        /// facing. It never fires on the action's own timeline; the projectile effect naming it in <c>impactEffectId</c>
        /// places it on a hit, a wall or the end of its range (not when its owner is gone).
        /// </summary>
        Impact = 8,

        /// <summary>
        /// The point a <see cref="ActionKind.UseItem"/> action uses its item (spec 3.4 "Consumable use", WP-M3-3): the server
        /// removes one unit and applies the <see cref="ConsumableDef"/> effects. Only on a useItem action, exactly once; no
        /// other field. Interrupted or cancelled before it fires, nothing is used.
        /// </summary>
        Consume = 9,

        /// <summary>
        /// The point a <see cref="ActionKind.Harvest"/> action harvests its node (spec 5.2, WP-M3-5): the server rolls the
        /// yield from the node's <see cref="NodeYieldEntry"/> table, adds it and depletes the node. Only on a harvest action,
        /// exactly once; no other field. Broken before it fires, nothing happens.
        /// </summary>
        Harvest = 10,
    }

    /// <summary>
    /// Which way a <see cref="EffectKind.Dash"/> moves the caster. Config enum (camelCase names); append only. Both are
    /// decided on the tick the action starts, from that tick's input, so the server and prediction agree.
    /// R-3.3-02 (user decision 2026-10-26): the dodge and the Archer's Dash use MoveInput with a 180 degree offset (no key
    /// held: backward); the Back Step uses Facing with 180.
    /// </summary>
    public enum DashDirection : byte
    {
        /// <summary>Along the facing locked at the start, turned by <c>directionOffsetDeg</c> (180: the Archer's Back Step).</summary>
        Facing = 0,

        /// <summary>
        /// Along the movement keys held on the tick the action starts (world axes, R-2.2-01: W is +Y, D is +X); with no key
        /// held, along the locked facing turned by <c>directionOffsetDeg</c> (0: forward, 180: a step back).
        /// </summary>
        MoveInput = 1,
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
    /// Who a heal, a status effect or a ground area affects (R-3.1-07 target filter, user decision 2026-10-22). Config enum
    /// (camelCase names); append only. <see cref="Self"/> is the caster, whatever the shapes; <see cref="Allies"/> are the
    /// living entities of the caster's faction other than itself inside the shapes (M1: every other player, R-3.1-07; monsters
    /// for a monster); <see cref="Enemies"/> are the ones a strike could hit (<c>HitResolver.CanHit</c>).
    /// </summary>
    public enum EffectTarget : byte
    {
        Self = 1,
        Allies = 2,
        Enemies = 3,
    }

    /// <summary>
    /// One effect of an action (spec 3.1 <c>ActionEffectDef</c>, R-3.1-07): pinned to a phase of the action and an offset
    /// inside it. It fires on the tick its phase reaches the offset (rounded up to whole ticks, never past the phase);
    /// effects that have not fired when the action is cancelled or interrupted are dropped. Config rows hold no nested
    /// objects, so the parameters of every kind are flat fields here; a field the kind does not use must be 0 or empty.
    /// <list type="bullet">
    /// <item>Strike: <see cref="PoiseDamage"/>, <see cref="FlinchPower"/>, <see cref="FlinchMs"/>, <see cref="DamagePercent"/>, <see cref="DamageKind"/> (all required), <see cref="Element"/>; at least one <see cref="HitShapeDef"/> row (R-3.1-03).</item>
    /// <item>Dash: <see cref="DistanceCm"/> over <see cref="DurationMs"/>, inside the action, along the facing turned by <see cref="DirectionOffsetDeg"/> or along the movement keys held at the start (<see cref="DashDirection"/>).</item>
    /// <item>GroundArea: <see cref="Targets"/>, <see cref="DurationMs"/>, <see cref="TickIntervalMs"/>; per tick on enemies <see cref="DamagePercent"/> and <see cref="DamageKind"/> (required with <c>enemies</c>), <see cref="PoiseDamage"/>, <see cref="Element"/>; per tick on the caster or allies <see cref="HealPercent"/> (required with <c>self</c> or <c>allies</c>); optional <see cref="VisualId"/>; at least one <see cref="HitShapeDef"/> row.</item>
    /// <item>Heal: <see cref="Targets"/> (never enemies) and <see cref="HealPercent"/>; optional <see cref="BuildUpReduction"/>.</item>
    /// <item>ApplyStatus: <see cref="Targets"/> and <see cref="StatusId"/>.</item>
    /// <item>RemoveStatus: <see cref="Targets"/> and either <see cref="StatusId"/> or <see cref="StatusKind"/> (R-3.7-03), and/or <see cref="BuildUpReduction"/> (R-3.8-04).</item>
    /// <item>Projectile: <see cref="ProjectileId"/>, and the <see cref="PoiseDamage"/>, <see cref="FlinchPower"/>, <see cref="FlinchMs"/>, <see cref="DamagePercent"/>, <see cref="DamageKind"/> of each of its hits (R-3.1-08, R-3.2-02, all required), <see cref="Element"/>.</item>
    /// </list>
    /// Heal, ApplyStatus and RemoveStatus are not hits (user decision 2026-10-23): they land in tick phase 6 on whoever their
    /// targets cover, with no dodge, parry or pending hit. They have shapes when they reach allies or enemies, none when they
    /// only affect the caster. The fields added in version 2, and by WP11 without a version change (so the rows and tests of
    /// parallel branches stay valid), are <see cref="OptionalAttribute"/>, so rows written without them stay valid. Build-up
    /// changes per hit (spec 3.8) are <see cref="EffectBuildUpDef"/> rows. TODO: the flat damage of step 1.
    /// </summary>
    [Table("ActionEffectDef", 2)]
    public sealed class ActionEffectDef : IValidatableRow, ISetValidatableRow
    {
        /// <summary>Effects per action the ActionMachine schedules (it tracks fired effects in one byte).</summary>
        public const int MaxEffectsPerAction = 8;

        /// <summary>Upper bound of <see cref="HealPercent"/> (the damage multiplier's overflow limit).</summary>
        public const int MaxHealPercent = DamageCalc.MaxSkillMultiplierPercent;

        [TableId] public string Id { get; set; } = string.Empty;

        [Ref(typeof(ActionDef))] public string ActionId { get; set; } = string.Empty;

        public EffectKind Kind { get; set; }

        public ActionPhase Phase { get; set; }

        /// <summary>From the start of <see cref="Phase"/>; must be shorter than the phase.</summary>
        [Range(0, 60_000)] public int OffsetMs { get; set; }

        [Range(0, 5_000)] public int DistanceCm { get; set; }

        /// <summary>
        /// Dash only: the move's direction relative to the facing locked at the start, in whole degrees clockwise (0 forward,
        /// 180 backward: the Archer's Back Step). With <see cref="DashDirection.MoveInput"/> it turns only the fallback taken
        /// when no movement key is held.
        /// </summary>
        [Optional, Range(0, 359)] public int DirectionOffsetDeg { get; set; }

        /// <summary>
        /// Dash only: along the locked facing (the default) or along the movement keys held at the start
        /// (<see cref="Tables.DashDirection"/>). The dodge and the Archer's Dash: MoveInput (R-3.3-02).
        /// </summary>
        [Optional] public DashDirection DashDirection { get; set; } = DashDirection.Facing;
        [Range(0, 600_000)] public int DurationMs { get; set; }
        [Range(0, 60_000)] public int TickIntervalMs { get; set; }
        [Range(0, 10_000)] public int PoiseDamage { get; set; }
        [Range(0, 10_000)] public int FlinchPower { get; set; }
        [Range(0, 10_000)] public int FlinchMs { get; set; }

        /// <summary>
        /// The status an ApplyStatus applies or a RemoveStatus removes (spec 3.7). On a Strike, Projectile or Impact it is an
        /// on-hit status (WP-M2-9): a debuff applied to each target the hit lands on, never on a dodged or parried hit (full
        /// or reduced; user answer 2026-10-28) or a killing one. Empty for the other kinds.
        /// </summary>
        [Ref(typeof(StatusEffectDef), Optional = true)] public string StatusId { get; set; } = string.Empty;

        /// <summary>
        /// R-3.4-04 step 1, the skill multiplier in whole percents (100 = x1) on the attacker's Atk or Matk. Strike,
        /// Projectile, and a GroundArea's ticks on enemies only (bounded by the damage formula's overflow limit).
        /// </summary>
        [Range(0, DamageCalc.MaxSkillMultiplierPercent)] public int DamagePercent { get; set; }

        /// <summary>R-3.4-04 step 1: Atk or Matk. Strike, Projectile and a GroundArea that reaches enemies only.</summary>
        public EffectDamageKind DamageKind { get; set; }

        /// <summary>True for the kinds that always deal damage through the hit pipeline of spec 3.1.</summary>
        public static bool DealsDamage(EffectKind kind) => kind == EffectKind.Strike || kind == EffectKind.Projectile
            || kind == EffectKind.Impact;

        /// <summary>True for the kinds the action machine fires on the action's timeline (all but Impact, WP-M2-9).</summary>
        public static bool FiresOnTimeline(EffectKind kind) => kind != EffectKind.Impact;

        /// <summary>
        /// True when this effect deals damage through the hit pipeline: a Strike, a Projectile, or a GroundArea that reaches
        /// enemies (its ticks, spec 3.1). Only these carry build-up changes (<see cref="EffectBuildUpDef"/>, spec 3.8).
        /// </summary>
        public bool HitsEnemies() => DealsDamage(Kind) || Kind == EffectKind.GroundArea && HasTarget(EffectTarget.Enemies);

        /// <summary>True for a GroundArea that heals the caster or allies on each of its ticks (spec 3.1).</summary>
        public bool HealsPerTick() => Kind == EffectKind.GroundArea && (HasTarget(EffectTarget.Self) || HasTarget(EffectTarget.Allies));

        /// <summary>The projectile a <see cref="EffectKind.Projectile"/> spawns (spec 3.2); empty for every other kind.</summary>
        [Ref(typeof(ProjectileDef), Optional = true)] public string ProjectileId { get; set; } = string.Empty;

        /// <summary>
        /// R-3.4-04 step 5 (R-3.4-07): the element of a Strike, Projectile or GroundArea; <c>none</c> (the default) for
        /// non-elemental damage and every other kind.
        /// </summary>
        [Optional] public Element Element { get; set; } = Element.None;

        /// <summary>
        /// R-3.1-07 target filter of a Heal, ApplyStatus, RemoveStatus or GroundArea (one or more, no repeats); empty for
        /// the other kinds (strikes and projectiles always hit enemies).
        /// </summary>
        [Optional] public EffectTarget[] Targets { get; set; } = Array.Empty<EffectTarget>();

        /// <summary>A Heal restores this percent of the caster's Matk (100 = x1), rounded down; 0 for the other kinds.</summary>
        [Optional, Range(0, MaxHealPercent)] public int HealPercent { get; set; }

        /// <summary>
        /// A RemoveStatus without <see cref="StatusId"/> removes dispellable statuses of this kind: <c>debuff</c> to
        /// cleanse, <c>buff</c> to strip (R-3.7-03), the newest first (spec 3.7 M2 status list), at most
        /// <see cref="RemoveCount"/>; <c>none</c> otherwise.
        /// </summary>
        [Optional] public StatusEffectKind StatusKind { get; set; } = StatusEffectKind.None;

        /// <summary>
        /// WP-M2-9 (spec 3.7 M2 status list): how many statuses a RemoveStatus by <see cref="StatusKind"/> removes from each
        /// target, the newest first; 0 removes them all (a full cleanse; Simple Cleanse: 2). 0 for every other effect.
        /// </summary>
        [Optional, Range(0, StatusEffectDef.MaxStackLimit)] public int RemoveCount { get; set; }

        /// <summary>
        /// WP-M2-9 (Concentration, MP regeneration while channeling): a self-only ApplyStatus whose status ends when the
        /// caster's action leaves this effect's phase (it completes, is cancelled or is interrupted). The status lasts at
        /// least that phase. False for every other effect.
        /// </summary>
        [Optional] public bool EndsWithPhase { get; set; }

        /// <summary>
        /// WP-M2-9 (the explosion on impact): a Projectile names the <see cref="EffectKind.Impact"/> effect of the same action
        /// placed where it ends; empty for none and for every other kind.
        /// </summary>
        [Optional, Ref(typeof(ActionEffectDef), Optional = true)] public string ImpactEffectId { get; set; } = string.Empty;

        /// <summary>
        /// R-3.8-04: a RemoveStatus or Heal lowers every build-up meter of each of its targets by this many points (0 to
        /// <see cref="BuildUpDef.MeterMax"/>; Simple Cleanse: 40); 0 for none and for every other kind. Not scaled by
        /// resistance; a meter locked by its running effect is not lowered (spec 3.8).
        /// </summary>
        [Optional, Range(0, BuildUpDef.MeterMax)] public int BuildUpReduction { get; set; }

        /// <summary>
        /// A GroundArea's look (its <see cref="EntityVisualDef"/> row, client only); empty draws the placeholder built from its
        /// hit shapes. Swapping the art is data only. Empty for every other kind.
        /// </summary>
        [Optional, Ref(typeof(EntityVisualDef), Optional = true)] public string VisualId { get; set; } = string.Empty;

        /// <summary>True when <see cref="Targets"/> lists <paramref name="target"/>.</summary>
        public bool HasTarget(EffectTarget target) => Array.IndexOf(Targets, target) >= 0;

        /// <summary>
        /// True when the effect is resolved from its hit shapes (R-3.1-03): strikes, ground areas, and heals or status
        /// effects that reach allies or enemies. A self-only effect has none (R-3.1-07).
        /// </summary>
        public bool TakesShapes()
        {
            if (Kind == EffectKind.Strike || Kind == EffectKind.GroundArea || Kind == EffectKind.Impact)
                return true;
            if (Kind != EffectKind.Heal && Kind != EffectKind.ApplyStatus && Kind != EffectKind.RemoveStatus)
                return false;
            return HasTarget(EffectTarget.Allies) || HasTarget(EffectTarget.Enemies);
        }

        /// <summary>The kinds that take a <see cref="Targets"/> filter (R-3.1-07).</summary>
        public static bool IsFiltered(EffectKind kind) => kind == EffectKind.Heal || kind == EffectKind.ApplyStatus
            || kind == EffectKind.RemoveStatus || kind == EffectKind.GroundArea;

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
            if (HitsEnemies())
            {
                Require(errors, nameof(DamagePercent), DamagePercent, $"a {Kind} that hits enemies needs a damage multiplier (R-3.4-04)");
                if (DamageKind != EffectDamageKind.Physical && DamageKind != EffectDamageKind.Magic)
                    errors.Add(nameof(DamageKind), $"a {Kind} deals physical or magic damage (R-3.4-04)");
            }
            else
            {
                if (DamagePercent != 0) errors.Add(nameof(DamagePercent), $"must be 0 for a {Kind} that hits no enemies");
                if (DamageKind != EffectDamageKind.None) errors.Add(nameof(DamageKind), $"must be none for a {Kind} that hits no enemies");
            }
            if ((uint)Element > (uint)Elements.Last)
                errors.Add(nameof(Element), "unknown element");
            else if (Element != Element.None && !HitsEnemies())
                errors.Add(nameof(Element), $"must be none for {Kind} (only damaging effects have an element, R-3.4-07)");
            CheckTargets(errors);
            if (Kind == EffectKind.Heal)
                Require(errors, nameof(HealPercent), HealPercent, "a heal needs an amount (percent of Matk)");
            else if (HealsPerTick())
                Require(errors, nameof(HealPercent), HealPercent, "a ground area on self or allies heals them each tick (percent of Matk)");
            else if (HealPercent != 0)
                errors.Add(nameof(HealPercent), $"must be 0 for a {Kind} that heals no one");
            if (BuildUpReduction != 0 && Kind != EffectKind.RemoveStatus && Kind != EffectKind.Heal)
                errors.Add(nameof(BuildUpReduction), $"must be 0 for {Kind} (RemoveStatus and Heal lower build-ups, R-3.8-04)");
            if (VisualId.Length != 0 && Kind != EffectKind.GroundArea)
                errors.Add(nameof(VisualId), $"must be empty for {Kind} (only ground areas have a visual of their own)");
            if (Kind != EffectKind.RemoveStatus && StatusKind != StatusEffectKind.None)
                errors.Add(nameof(StatusKind), $"must be none for {Kind}");
            if (RemoveCount != 0 && (Kind != EffectKind.RemoveStatus || StatusKind == StatusEffectKind.None))
                errors.Add(nameof(RemoveCount), "only a RemoveStatus by statusKind removes a count (spec 3.7)");
            if (EndsWithPhase && (Kind != EffectKind.ApplyStatus || Targets.Length != 1 || Targets[0] != EffectTarget.Self))
                errors.Add(nameof(EndsWithPhase), "only a self-only ApplyStatus ends with its phase");
            if (ImpactEffectId.Length != 0 && Kind != EffectKind.Projectile)
                errors.Add(nameof(ImpactEffectId), $"must be empty for {Kind} (only a projectile has an impact)");
            switch (Kind)
            {
                case EffectKind.Strike:
                case EffectKind.Impact:
                    // R-3.1-08: every hit carries poise damage; R-3.3-07: and a flinch power and a base flinch time.
                    // StatusId is the on-hit status (WP-M2-9).
                    Require(errors, nameof(PoiseDamage), PoiseDamage, "every hit carries poise damage (R-3.1-08)");
                    Require(errors, nameof(FlinchPower), FlinchPower, "every hit carries a flinch power (R-3.3-07)");
                    Require(errors, nameof(FlinchMs), FlinchMs, "every hit carries a base flinch time (R-3.3-07)");
                    Unused(errors, distance: true, duration: true, tickInterval: true);
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
                    if (string.IsNullOrWhiteSpace(StatusId))
                        errors.Add(nameof(StatusId), "must name a status");
                    Unused(errors, distance: true, duration: true, tickInterval: true, poise: true, flinch: true);
                    break;
                case EffectKind.RemoveStatus:
                    // R-3.7-03: one named status, or every dispellable buff (strip) or debuff (cleanse); R-3.8-04: and/or build-ups.
                    if (!string.IsNullOrWhiteSpace(StatusId) && StatusKind != StatusEffectKind.None)
                        errors.Add(nameof(StatusId), "must name either a status or a statusKind (buff or debuff), not both");
                    else if (string.IsNullOrWhiteSpace(StatusId) && StatusKind == StatusEffectKind.None && BuildUpReduction == 0)
                        errors.Add(nameof(StatusId), "must name a status, a statusKind (buff or debuff) or a buildUpReduction");
                    Unused(errors, distance: true, duration: true, tickInterval: true, poise: true, flinch: true);
                    break;
                case EffectKind.Projectile:
                    // R-3.1-08 / R-3.2-02: a projectile hits through the normal rules, so each hit carries poise and flinch.
                    if (string.IsNullOrWhiteSpace(ProjectileId))
                        errors.Add(nameof(ProjectileId), "a projectile effect must name a ProjectileDef");
                    Require(errors, nameof(PoiseDamage), PoiseDamage, "every hit carries poise damage (R-3.1-08)");
                    Require(errors, nameof(FlinchPower), FlinchPower, "every hit carries a flinch power (R-3.3-07)");
                    Require(errors, nameof(FlinchMs), FlinchMs, "every hit carries a base flinch time (R-3.3-07)");
                    Unused(errors, distance: true, duration: true, tickInterval: true);
                    break;
                case EffectKind.Heal:
                    Unused(errors, distance: true, duration: true, tickInterval: true, poise: true, flinch: true, status: true);
                    break;
                case EffectKind.Consume:
                    // Spec 3.4 consumable use: what it does is the item's ConsumableDef row.
                    Unused(errors, distance: true, duration: true, tickInterval: true, poise: true, flinch: true, status: true);
                    break;
                case EffectKind.Harvest:
                    // Spec 5.2 harvesting: what it yields is the node's NodeYieldEntry table.
                    Unused(errors, distance: true, duration: true, tickInterval: true, poise: true, flinch: true, status: true);
                    break;
                default:
                    errors.Add(nameof(Kind), "unknown effect kind");
                    break;
            }
            if (Kind != EffectKind.Projectile && ProjectileId.Length != 0)
                errors.Add(nameof(ProjectileId), $"must be empty for {Kind}");
            if (Kind != EffectKind.Dash && DirectionOffsetDeg != 0)
                errors.Add(nameof(DirectionOffsetDeg), $"must be 0 for {Kind}");
            if (DashDirection != DashDirection.Facing && DashDirection != DashDirection.MoveInput)
                errors.Add(nameof(DashDirection), "unknown dash direction");
            else if (Kind != EffectKind.Dash && DashDirection != DashDirection.Facing)
                errors.Add(nameof(DashDirection), $"must be facing for {Kind}");
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
            if (Kind == EffectKind.Consume && action.Kind != ActionKind.UseItem)
                errors.Add(nameof(Kind), $"a consume effect belongs to a useItem action, '{ActionId}' is a {action.Kind}");
            if (Kind == EffectKind.Harvest && action.Kind != ActionKind.Harvest)
                errors.Add(nameof(Kind), $"a harvest effect belongs to a harvest action, '{ActionId}' is a {action.Kind}");
            if (Kind == EffectKind.Dash && action.PhaseStartMs(Phase) + OffsetMs + DurationMs > action.TotalMs())
                errors.Add(nameof(DurationMs), $"the dash must end before '{ActionId}' ends");
            // R-3.1-03, R-3.1-07: effects that reach others are resolved from shapes. CountFor is -1 when HitShapeDef is not loaded.
            if (TakesShapes() && HitShapeDef.CountFor(set, Id) == 0)
                errors.Add(nameof(Id), $"a {Kind} that reaches others needs at least one HitShapeDef row");
            CheckStatus(set, action, errors);

            if (!set.TryGet(typeof(ActionEffectDef), out var table))
                return;
            int count = 0, namedBy = 0;
            ActionEffectDef? impact = null;
            foreach (var row in table.Rows)
            {
                var other = (ActionEffectDef)row;
                if (other.ActionId == ActionId)
                    count++;
                if (other.ImpactEffectId == Id)
                    namedBy++;
                if (ImpactEffectId.Length != 0 && other.Id == ImpactEffectId)
                    impact = other;
            }
            if (count > MaxEffectsPerAction)
                errors.Add(nameof(ActionId), $"'{ActionId}' has {count} effects, at most {MaxEffectsPerAction}");
            // WP-M2-9: an Impact belongs to exactly one projectile effect of its own action.
            if (Kind == EffectKind.Impact && namedBy != 1)
                errors.Add(nameof(Id), $"an Impact must be named by exactly one projectile effect's impactEffectId ({namedBy})");
            if (impact != null && (impact.Kind != EffectKind.Impact || impact.ActionId != ActionId))
                errors.Add(nameof(ImpactEffectId), $"must name an Impact effect of '{ActionId}'");
        }

        private void CheckStatus(ConfigSet set, ActionDef action, RowErrors errors)
        {
            if (StatusId.Length == 0)
                return;
            var status = StatusEffectDef.Find(set, StatusId);
            if (status == null)
                return; // reported as BrokenRef
            if (DealsDamage(Kind) && status.Kind != StatusEffectKind.Debuff)
                errors.Add(nameof(StatusId), "an on-hit status must be a debuff (it lands on enemies)");
            if (EndsWithPhase && status.DurationMs < action.PhaseMs(Phase) - OffsetMs)
                errors.Add(nameof(StatusId), $"'{StatusId}' must last at least the rest of the {Phase} phase it ends with");
        }

        private void CheckTargets(RowErrors errors)
        {
            if (!IsFiltered(Kind))
            {
                if (Targets.Length != 0)
                    errors.Add(nameof(Targets), $"must be empty for {Kind}");
                return;
            }
            if (Targets.Length == 0)
                errors.Add(nameof(Targets), $"a {Kind} must name who it affects: self, allies or enemies (R-3.1-07)");
            for (int i = 0; i < Targets.Length; i++)
            {
                if (Targets[i] != EffectTarget.Self && Targets[i] != EffectTarget.Allies && Targets[i] != EffectTarget.Enemies)
                    errors.Add(nameof(Targets), "unknown target");
                else if (Array.IndexOf(Targets, Targets[i]) != i)
                    errors.Add(nameof(Targets), $"lists {Targets[i]} twice");
            }
            if (Kind == EffectKind.Heal && HasTarget(EffectTarget.Enemies))
                errors.Add(nameof(Targets), "a heal never targets enemies");
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
