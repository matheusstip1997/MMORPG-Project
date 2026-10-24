using System;
using Shared.Simulation.Stats;

namespace Shared.Data.Tables
{
    /// <summary>
    /// A monster type (catalog 8.5, backlog F): stats, poise and flinch (R-3.3-01, R-3.3-07), its look, and what the generic
    /// monster AI needs: its attacks, speed, aggro and leash ranges. Spawned at <see cref="SpawnPoints"/>; respawns after
    /// <c>StartingValuesM1.monsterRespawnMs</c>, or <c>bossRespawnMs</c> for a <see cref="Boss"/> (spec 12.7). Placeholder
    /// numbers. The AI's shared settings are in <see cref="MonsterAiSettings"/>.
    /// TODO(open): whether monsters use stamina (spec 3.1 only says actions cost stamina); their stamina stat stays at the
    /// <c>StatDef</c> minimum and their actions pay nothing. Attack colors are the attacks' <c>ActionDef.parryClassPve</c> (R-3.3-04, R-3.3-06). TODO(F): level and the recommended
    /// level range (spec 3.6), loot tables (G).
    /// </summary>
    [Table("MonsterDef", 2)]
    public sealed class MonsterDef : IValidatableRow, ISetValidatableRow, ICombatantTemplate
    {
        public const int MaxNetId = ClassDef.MaxNetId;

        /// <summary>Stable id, for example <c>m1.demon</c>.</summary>
        [TableId] public string Id { get; set; } = string.Empty;

        /// <summary>Definition id of the monster's spawn event (spec 1.7), so clients pick its visual; unique, never reused.</summary>
        [Range(1, MaxNetId)] public int NetId { get; set; }

        [Ref(typeof(EntityVisualDef))] public string VisualId { get; set; } = string.Empty;

        /// <summary>Monsters have ranks too (spec 3.6); steps 3 and 4 of the damage formula read it.</summary>
        public Rank Rank { get; set; }

        /// <summary>Bosses respawn after <c>bossRespawnMs</c> (spec 12.7) and get the boss bar (spec 9).</summary>
        public bool Boss { get; set; }

        [Range(1, 1_000_000_000)] public int MaxHp { get; set; }
        [Range(0, 1_000_000)] public int MaxMp { get; set; }
        [Range(0, 100_000)] public int HpRegen { get; set; }
        [Range(0, 100_000)] public int MpRegen { get; set; }
        [Range(0, 1_000_000)] public int Atk { get; set; }
        [Range(0, 1_000_000)] public int Matk { get; set; }
        [Range(0, 1_000_000)] public int Def { get; set; }
        [Range(0, 1_000_000)] public int Mdef { get; set; }

        /// <summary>R-3.3-01: size of the poise bar.</summary>
        [Range(1, 1_000_000)] public int PoiseMax { get; set; }

        /// <summary>R-3.3-07: a hit flinches when its <c>flinchPower</c> is at least this.</summary>
        [Range(0, 10_000)] public int FlinchResist { get; set; }

        /// <summary>R-3.3-07: a flinch lasts the hit's <c>flinchMs</c> times this / 100.</summary>
        [Range(0, 1_000)] public int FlinchDurationPercent { get; set; }

        /// <summary>R-3.3-07: after a flinch, further flinches are ignored this long.</summary>
        [Range(0, 60_000)] public int FlinchImmunityMs { get; set; }

        /// <summary>The body circle hit shapes test against (R-3.1-03, spec 3.1: a target is hit when its circle touches a shape).</summary>
        [Range(1, ClassDef.MaxBodyRadiusCm)] public int BodyRadiusCm { get; set; }

        /// <summary>
        /// The actions the AI attacks with (backlog F "attack by action table"), in priority order: it starts the first one
        /// that is off cooldown and reaches its target. Each is an attack or a skill with a strike or a projectile effect,
        /// and its startup is at least <c>StartingValuesM1.monsterAttackStartupMinMs</c> (R-3.1-03). A monster with a spawn
        /// point has at least one.
        /// </summary>
        [Ref(typeof(ActionDef))] public string[] Attacks { get; set; } = Array.Empty<string>();

        /// <summary>Speed of the AI's walk (the chase and the walk home).</summary>
        [Range(1, 10_000)] public int MoveSpeedCmPerSec { get; set; }

        /// <summary>An idle monster aggroes the nearest player it can see this close (R-2.4-03).</summary>
        [Range(1, 100_000)] public int AggroRangeCm { get; set; }

        /// <summary>Farther than this from its spawn point the monster drops its target and walks home (leash).</summary>
        [Range(1, 100_000)] public int LeashRangeCm { get; set; }

        /// <summary>
        /// R-3.7-02: statuses with one of these tags (<c>StatusEffectDef.tags</c>) are not applied to this monster (mainly
        /// bosses). Optional; empty by default.
        /// </summary>
        [Optional] public string[] StatusImmuneTags { get; set; } = Array.Empty<string>();

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
            if (!Enum.IsDefined(typeof(Rank), Rank))
                errors.Add(nameof(Rank), $"{(int)Rank} is not a rank");
            if (ActionDef.HasDuplicates(Attacks))
                errors.Add(nameof(Attacks), "lists an action twice");
            if (LeashRangeCm <= AggroRangeCm)
                errors.Add(nameof(LeashRangeCm), "must be greater than AggroRangeCm, or the monster leashes as it aggroes");
        }

        public void Validate(ConfigSet set, RowErrors errors)
        {
            NetIds.CheckUnique<MonsterDef>(set, this, NetId, row => row.NetId, row => row.Id, errors);
            CheckAttacks(set, errors);
            if (Attacks.Length == 0 && HasSpawnPoint(set))
                errors.Add(nameof(Attacks), "a monster with a spawn point needs at least one attack");
        }

        public static MonsterDef? Find(ConfigSet set, string id) => NetIds.Find<MonsterDef>(set, id, row => row.Id);

        /// <summary>
        /// R-3.1-03: a monster attack's startup must exceed reaction time plus the lag compensation cap (spec 12.7
        /// <c>monsterAttackStartupMinMs</c>), so a defender always has time to dodge or parry. The rule lives here, not on
        /// ActionDef, because only this table knows which actions are monster attacks. Missing actions are reported by the
        /// <c>[Ref]</c> check; rules that need a table not loaded are skipped, like the other cross-table rules.
        /// </summary>
        private void CheckAttacks(ConfigSet set, RowErrors errors)
        {
            int minStartupMs = -1;
            if (set.TryGet(typeof(StartingValuesM1), out var valuesTable) && valuesTable.Rows.Count == 1)
                minStartupMs = ((StartingValuesM1)valuesTable.Rows[0]).MonsterAttackStartupMinMs;
            bool hasEffects = set.TryGet(typeof(ActionEffectDef), out var effects);
            foreach (var id in Attacks)
            {
                var action = ActionDef.Find(set, id);
                if (action == null)
                    continue;
                if (action.Kind != ActionKind.Attack && action.Kind != ActionKind.Skill)
                    errors.Add(nameof(Attacks), $"'{id}' is a {action.Kind} action, not an attack or a skill");
                if (minStartupMs >= 0 && action.StartupMs < minStartupMs)
                    errors.Add(nameof(Attacks), $"'{id}' has a startup of {action.StartupMs} ms, below "
                        + $"StartingValuesM1.MonsterAttackStartupMinMs ({minStartupMs} ms, R-3.1-03)");
                if (hasEffects && !HasHittingEffect(effects, id))
                    errors.Add(nameof(Attacks), $"'{id}' has no strike or projectile effect, so it cannot hit anything");
            }
        }

        private static bool HasHittingEffect(ConfigTable effects, string actionId)
        {
            foreach (var row in effects.Rows)
            {
                var effect = (ActionEffectDef)row;
                if (effect.ActionId == actionId && (effect.Kind == EffectKind.Strike || effect.Kind == EffectKind.Projectile))
                    return true;
            }
            return false;
        }

        private bool HasSpawnPoint(ConfigSet set)
        {
            if (!set.TryGet(typeof(SpawnPoints), out var points))
                return false;
            foreach (var row in points.Rows)
            {
                if (((SpawnPoints)row).MonsterId == Id)
                    return true;
            }
            return false;
        }
    }
}
