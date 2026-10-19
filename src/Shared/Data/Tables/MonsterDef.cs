using Shared.Simulation.Stats;

namespace Shared.Data.Tables
{
    /// <summary>
    /// A monster type (catalog 8.5, backlog F) v0: stats, poise and flinch (R-3.3-01, R-3.3-07) and its look. Spawned at
    /// <see cref="SpawnPoints"/>; respawns after <c>StartingValuesM1.monsterRespawnMs</c>, or <c>bossRespawnMs</c> for a
    /// <see cref="Boss"/> (spec 12.7). Placeholder numbers.
    /// TODO(F): the AI action set (aggro, chase, attacks by action table, leash) and attack colors; monsters stand still.
    /// TODO(open): whether monsters use stamina (spec 3.1 only says actions cost stamina); their stamina stat stays at the
    /// <c>StatDef</c> minimum. TODO(F): level and the recommended level range (spec 3.6), loot tables (G).
    /// </summary>
    [Table("MonsterDef", 1)]
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

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
            if (!System.Enum.IsDefined(typeof(Rank), Rank))
                errors.Add(nameof(Rank), $"{(int)Rank} is not a rank");
        }

        public void Validate(ConfigSet set, RowErrors errors) =>
            NetIds.CheckUnique<MonsterDef>(set, this, NetId, row => row.NetId, row => row.Id, errors);

        public static MonsterDef? Find(ConfigSet set, string id) => NetIds.Find<MonsterDef>(set, id, row => row.Id);
    }
}
