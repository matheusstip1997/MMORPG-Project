using System;
using Shared.Simulation.Stats;

namespace Shared.Data.Tables
{
    /// <summary>
    /// Buff or debuff (spec 3.7, R-3.7-01; buff row border, spec 9). Config enum (camelCase names); append only.
    /// <see cref="None"/> is only for filters (<c>ActionEffectDef.statusKind</c>); a status is a buff or a debuff.
    /// </summary>
    public enum StatusEffectKind : byte
    {
        None = 0,
        Buff = 1,
        Debuff = 2,
    }

    /// <summary>
    /// What a control status does besides its stats (spec 3.7 "M2 status list", WP-M2-9). Config enum (camelCase names);
    /// append only. A control status stacks at most <see cref="StatusSettings.ControlMaxStacks"/>.
    /// </summary>
    public enum StatusControl : byte
    {
        None = 0,
        /// <summary>No actions or movement while it lasts: the action machine is <c>Stunned</c> (it goes through super armor).</summary>
        Stun = 1,
        /// <summary>The target's strikes and projectiles hit nothing while it lasts (ground areas and statuses still land).</summary>
        Blind = 2,
        /// <summary>The monster AI targets the status's applier while it lasts (players: no effect).</summary>
        Provoke = 3,
    }

    /// <summary>
    /// One status effect (spec 3.7 <c>StatusEffectDef</c>, backlog D6). Applied by an <c>ApplyStatus</c> effect (or, from
    /// WP11, a build-up trigger), it lasts <see cref="DurationMs"/> (rounded up to whole ticks). Applying it again to a target
    /// that has it refreshes the duration and, below <see cref="MaxStacks"/>, adds a stack; there is one instance per status
    /// per target, owned by its newest applier (user decision 2026-10-23). It changes stats through its
    /// <see cref="StatusModifierDef"/> rows (per stack) and, with <see cref="TickIntervalMs"/>, deals damage or heals every
    /// interval, from the applier's Atk or Matk captured when it was applied: damage through <c>DamageCalc</c> with
    /// <see cref="TickElement"/> and never a crit, credited to the applier (R-3.8-09); heals never crit either.
    /// <para>
    /// It ends when it runs out, when it is cleansed or stripped (<see cref="Dispellable"/>, R-3.7-03), and on death, logout
    /// (statuses are not saved) and a monster's leash reset (user decision 2026-10-23). A monster whose
    /// <c>statusImmuneTags</c> share a tag with <see cref="Tags"/> is immune (R-3.7-02). TODO(open): statuses that count
    /// stacks without a duration (R-3.7-01), pets and mounts as on/off states (R-3.7-04, M7).
    /// </para>
    /// </summary>
    [Table("StatusEffectDef", 1)]
    public sealed class StatusEffectDef : IValidatableRow, ISetValidatableRow
    {
        /// <summary>Wire limit of <see cref="NetId"/> (12 bits in the own-entity snapshot block, spec 1.7).</summary>
        public const int MaxNetId = 4095;

        /// <summary>Wire limit of <see cref="MaxStacks"/> (4 bits).</summary>
        public const int MaxStackLimit = 15;

        /// <summary>Longest status (the remaining ticks fit the 16-bit wire field at 20 Hz).</summary>
        public const int MaxDurationMs = 600_000;

        /// <summary>Stable id, for example <c>staminaBuff</c>.</summary>
        [TableId] public string Id { get; set; } = string.Empty;

        /// <summary>Id on the wire (own-entity snapshot block); unique.</summary>
        [Range(1, MaxNetId)] public int NetId { get; set; }

        /// <summary>Buff or debuff (never <c>none</c>).</summary>
        public StatusEffectKind Kind { get; set; }

        [Range(1, MaxDurationMs)] public int DurationMs { get; set; }

        /// <summary>1 for a status that only refreshes; more for one that stacks (modifiers times stacks).</summary>
        [Range(1, MaxStackLimit)] public int MaxStacks { get; set; }

        /// <summary>Cleanse (debuffs) and strip (buffs) by kind remove it (R-3.7-03); a RemoveStatus naming it always does.</summary>
        public bool Dispellable { get; set; }

        /// <summary>Free tags; immunity (R-3.7-02) matches them against <c>MonsterDef.statusImmuneTags</c>.</summary>
        public string[] Tags { get; set; } = Array.Empty<string>();

        /// <summary>Time between periodic ticks; 0 for none. The first tick comes one interval after the status is applied.</summary>
        [Range(0, 60_000)] public int TickIntervalMs { get; set; }

        /// <summary>Damage per tick in whole percents of the applier's Atk or Matk (<see cref="TickDamageKind"/>).</summary>
        [Range(0, DamageCalc.MaxSkillMultiplierPercent)] public int TickDamagePercent { get; set; }

        /// <summary>Atk or Matk for <see cref="TickDamagePercent"/>; <c>none</c> without tick damage.</summary>
        public EffectDamageKind TickDamageKind { get; set; }

        /// <summary>R-3.4-04 step 5 for the tick damage; <c>none</c> without tick damage or for non-elemental damage.</summary>
        public Element TickElement { get; set; }

        /// <summary>HP per tick in whole percents of the applier's Matk (works in combat, unlike the <c>hpRegen</c> stat).</summary>
        [Range(0, DamageCalc.MaxSkillMultiplierPercent)] public int TickHealPercent { get; set; }

        /// <summary>
        /// WP-M2-9: stun, blind or provoke (<see cref="StatusControl"/>); <c>none</c> for a status that only changes stats
        /// or ticks.
        /// </summary>
        [Optional] public StatusControl Control { get; set; } = StatusControl.None;

        /// <summary>
        /// WP-M2-9 (spec 3.8 M2 numbers, Cold): damage dealt once when the status is applied, in whole percents of the
        /// applier's Atk or Matk (<see cref="TickDamageKind"/>, <see cref="TickElement"/>), captured like the ticks; never a crit.
        /// </summary>
        [Optional, Range(0, DamageCalc.MaxSkillMultiplierPercent)] public int BurstDamagePercent { get; set; }

        /// <summary>
        /// WP-M2-9 (spec 3.8 M2 numbers, Blood loss): damage dealt once when the status is applied, in whole percents of the
        /// target's max HP; raw (no defence, resistance, crit or card bonus), capped on a boss by
        /// <c>MonsterDef.maxHpEffectCapPercent</c> (R-3.8-08).
        /// </summary>
        [Optional, Range(0, 100)] public int BurstMaxHpPercent { get; set; }

        /// <summary>True when the status deals damage or heals every <see cref="TickIntervalMs"/>.</summary>
        public bool HasTicks() => TickIntervalMs > 0;

        /// <summary>True when the status deals damage once when it is applied.</summary>
        public bool HasBurst() => BurstDamagePercent > 0 || BurstMaxHpPercent > 0;

        /// <summary>True when the status deals damage from the applier's Atk or Matk (ticks or a burst).</summary>
        public bool DealsPowerDamage() => TickDamagePercent > 0 || BurstDamagePercent > 0;

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
            if (Kind != StatusEffectKind.Buff && Kind != StatusEffectKind.Debuff)
                errors.Add(nameof(Kind), "must be buff or debuff");
            if (TickIntervalMs > DurationMs)
                errors.Add(nameof(TickIntervalMs), "must not be longer than the status lasts");
            if (HasTicks())
            {
                if ((TickDamagePercent > 0) == (TickHealPercent > 0))
                    errors.Add(nameof(TickDamagePercent), "a periodic status either damages or heals (one of tickDamagePercent, tickHealPercent)");
            }
            else if (TickDamagePercent != 0 || TickHealPercent != 0)
            {
                errors.Add(nameof(TickIntervalMs), "tick damage or healing needs a tick interval");
            }
            if (DealsPowerDamage())
            {
                if (TickDamageKind != EffectDamageKind.Physical && TickDamageKind != EffectDamageKind.Magic)
                    errors.Add(nameof(TickDamageKind), "tick and burst damage are physical or magic (R-3.4-04)");
            }
            else
            {
                if (TickDamageKind != EffectDamageKind.None)
                    errors.Add(nameof(TickDamageKind), "must be none without tick or burst damage");
                if (TickElement != Element.None)
                    errors.Add(nameof(TickElement), "must be none without tick or burst damage");
            }
            if ((uint)Control > (uint)StatusControl.Provoke)
                errors.Add(nameof(Control), "unknown control");
            else if (Control != StatusControl.None && Kind != StatusEffectKind.Debuff)
                errors.Add(nameof(Control), "a control status is a debuff (spec 3.7)");
            if ((uint)TickElement > (uint)Elements.Last)
                errors.Add(nameof(TickElement), "unknown element");
            for (int i = 0; i < Tags.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(Tags[i]))
                    errors.Add(nameof(Tags), "must not hold empty tags");
                else if (Array.IndexOf(Tags, Tags[i]) != i)
                    errors.Add(nameof(Tags), $"lists '{Tags[i]}' twice");
            }
        }

        public void Validate(ConfigSet set, RowErrors errors)
        {
            NetIds.CheckUnique<StatusEffectDef>(set, this, NetId, row => row.NetId, row => row.Id, errors);
            int modifiers = StatusModifierDef.CountFor(set, Id);
            if (modifiers == 0 && !HasTicks() && !HasBurst() && Control == StatusControl.None)
                errors.Add(nameof(Id), "a status needs at least one StatusModifierDef row, a periodic tick, a burst or a control");
            // Spec 3.7 M2 stack caps (user answer 2026-10-28: maxStacks limits).
            var settings = StatusSettings.Find(set);
            if (settings != null)
            {
                if (Control != StatusControl.None && MaxStacks > settings.ControlMaxStacks)
                    errors.Add(nameof(MaxStacks), $"a control status stacks at most {settings.ControlMaxStacks} (StatusSettings)");
                else if (Kind == StatusEffectKind.Debuff && modifiers > 0 && MaxStacks > settings.StatDebuffMaxStacks)
                    errors.Add(nameof(MaxStacks), $"a stat debuff stacks at most {settings.StatDebuffMaxStacks} (StatusSettings)");
            }
            int icons = BuffIconRegistry.CountFor(set, Id);
            if (icons == 0)
                errors.Add(nameof(Id), "needs a BuffIconRegistry row (its buff row icon, spec 9.7)");
        }

        /// <summary>True when one of <see cref="Tags"/> is in <paramref name="immuneTags"/> (R-3.7-02).</summary>
        public bool IsBlockedBy(string[] immuneTags)
        {
            for (int i = 0; i < Tags.Length; i++)
            {
                if (Array.IndexOf(immuneTags, Tags[i]) >= 0)
                    return true;
            }
            return false;
        }

        public static StatusEffectDef? Find(ConfigSet set, string id) => NetIds.Find<StatusEffectDef>(set, id, row => row.Id);
    }

    /// <summary>
    /// One stat change of a status (spec 3.7: "effects change stats through <c>StatModifier</c>s"), applied once per stack
    /// with source type buff or debuff. Config rows hold no nested objects, so a status with several changes has several
    /// rows (at most <see cref="MaxPerStatus"/>, one per stat).
    /// </summary>
    [Table("StatusModifierDef", 1)]
    public sealed class StatusModifierDef : IValidatableRow, ISetValidatableRow
    {
        public const int MaxPerStatus = 4;

        [TableId] public string Id { get; set; } = string.Empty;

        [Ref(typeof(StatusEffectDef))] public string StatusId { get; set; } = string.Empty;

        [Ref(typeof(StatDef))] public string Stat { get; set; } = string.Empty;

        /// <summary>Added to the stat's base, per stack (spec 3.4 formula).</summary>
        [Range(-1_000_000, 1_000_000)] public int Flat { get; set; }

        /// <summary>Whole percents of the stat, per stack; percents of all sources add up (spec 3.4).</summary>
        [Range(-100, 1_000)] public int Percent { get; set; }

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
            if (Flat == 0 && Percent == 0)
                errors.Add(nameof(Flat), "a modifier must change something (flat or percent)");
        }

        public void Validate(ConfigSet set, RowErrors errors)
        {
            if (!set.TryGet(typeof(StatusModifierDef), out var table))
                return;
            int count = 0;
            bool repeated = false;
            foreach (var row in table.Rows)
            {
                var other = (StatusModifierDef)row;
                if (other.StatusId != StatusId)
                    continue;
                count++;
                repeated |= !ReferenceEquals(other, this) && other.Stat == Stat;
            }
            if (repeated)
                errors.Add(nameof(Stat), $"'{StatusId}' changes '{Stat}' twice; use one row");
            if (count > MaxPerStatus)
                errors.Add(nameof(StatusId), $"'{StatusId}' has {count} modifiers, at most {MaxPerStatus}");
        }

        /// <summary>How many rows of the loaded table belong to the status, or -1 if the table is not loaded.</summary>
        public static int CountFor(ConfigSet set, string statusId)
        {
            if (!set.TryGet(typeof(StatusModifierDef), out var table))
                return -1;
            int count = 0;
            foreach (var row in table.Rows)
            {
                if (((StatusModifierDef)row).StatusId == statusId)
                    count++;
            }
            return count;
        }
    }

    /// <summary>
    /// How a status shows in the HUD buff row (spec 9.7 <c>BuffIconRegistry</c>): one row per <see cref="StatusEffectDef"/>.
    /// Client-only presentation data, swappable by data only: our own icons replace the placeholders by pointing the rows
    /// at new Addressables entries. Buff or debuff (the border, spec 9.3) comes from the status's kind. The most icons shown
    /// is <see cref="StatusSettings.BuffRowMaxIcons"/> (spec 9.7).
    /// </summary>
    [Table("BuffIconRegistry", 1)]
    public sealed class BuffIconRegistry : IValidatableRow
    {
        /// <summary>The status id.</summary>
        [TableId, Ref(typeof(StatusEffectDef))] public string Id { get; set; } = string.Empty;

        /// <summary>Addressables address of the icon; empty while it is a placeholder (the HUD then shows the status name).</summary>
        public string IconAddress { get; set; } = string.Empty;

        /// <summary>Lower first in the row.</summary>
        [Range(0, 10_000)] public int SortOrder { get; set; }

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
            if (IconAddress.Length != 0 && (IconAddress.Trim() != IconAddress || IconAddress.StartsWith("/", StringComparison.Ordinal)))
                errors.Add(nameof(IconAddress), "must be empty or an Addressables address without surrounding spaces or a leading /");
        }

        /// <summary>How many rows name the status, or -1 if the table is not loaded.</summary>
        public static int CountFor(ConfigSet set, string statusId)
        {
            if (!set.TryGet(typeof(BuffIconRegistry), out var table))
                return -1;
            int count = 0;
            foreach (var row in table.Rows)
            {
                if (((BuffIconRegistry)row).Id == statusId)
                    count++;
            }
            return count;
        }
    }
}
