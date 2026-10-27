using Shared.Data;
using Shared.Data.Tables;
using Shared.Simulation.Stats;

namespace Shared.Tests.Simulation.Stats;

/// <summary>
/// Small in-code tables for the stat and damage tests, so expected values are easy to compute by hand. Defaults:
/// C = 100 for rank F and 100 x (rank + 1) above, every rank factor 100, crit 5% / 150% (spec 12.7), crit chance cap
/// 75, crit damage bonus cap 150, the 10 elements of R-3.4-07 ("fire" first) capped at [-50, 75], stamina delay 1000 ms (spec 12.7),
/// HP delay 5000 ms, MP delay 3000 ms.
/// </summary>
internal static class StatsTestConfig
{
    /// <summary>The resistance stats of the 10 elements (R-3.4-07), "fireRes" first.</summary>
    public static readonly string[] ResistStats =
        { "fireRes", "iceRes", "waterRes", "windRes", "earthRes", "lightningRes", "holyRes", "darkRes", "bloodRes", "poisonRes" };

    public static readonly string[] ExtraStats = ResistStats.Append("str").ToArray();

    public static List<StatDef> StatRows()
    {
        // Damage dealt and taken go down to -100 (step 7 debuffs and buffs such as Magic Barrier), like data/tables.
        var rows = StatIds.Required.Select(id => new StatDef
        {
            // "str" keeps the small bounds the stat block tests clamp against (it is a required attribute stat now).
            Id = id,
            Min = id == StatIds.DamageDealtPercent || id == StatIds.DamageTakenPercent ? -100 : id == StatIds.Str ? -1_000 : 0,
            Max = id == StatIds.Str ? 1_000 : 1_000_000_000,
        }).ToList();
        rows.AddRange(ResistStats.Select(id => new StatDef { Id = id, Min = -900, Max = 100 }));
        return rows;
    }

    public static DamageConstants Damage() => new()
    {
        MitigationConstantByRank = new[] { 100, 200, 300, 400, 500, 600, 700, 800 },
        CritChanceCapPercent = 75,
        CritDamageBonusCapPercent = 150,
        RankFactorMinPercent = 50,
        RankFactorMaxPercent = 200,
    };

    public static List<RankFactor> RankRows(Func<Rank, Rank, int>? factor = null)
    {
        var rows = new List<RankFactor>();
        for (int a = 0; a < Ranks.Count; a++)
        for (int d = 0; d < Ranks.Count; d++)
            rows.Add(new RankFactor { Attacker = (Rank)a, Defender = (Rank)d, FactorPercent = factor?.Invoke((Rank)a, (Rank)d) ?? 100 });
        return rows;
    }

    public static List<ElementCaps> ElementRows() => ResistStats
        .Select(stat => new ElementCaps { Id = stat.Substring(0, stat.Length - 3), ResistStat = stat, DamageResistMinPercent = -50, DamageResistMaxPercent = 75 })
        .ToList();

    public static StartingValuesM1 Starting() => new()
    {
        CritChancePercent = 5,
        CritDamagePercent = 150,
        StaminaPool = 100,
        StaminaRegenPerSec = 20,
        StaminaRegenDelayMs = 1000,
    };

    public static ResourceRegen Regen() => new() { HpRegenDelayMs = 5000, MpRegenDelayMs = 3000 };

    public static ConfigSet Set(
        List<StatDef>? stats = null, DamageConstants? damage = null, List<RankFactor>? ranks = null,
        List<ElementCaps>? elements = null, StartingValuesM1? starting = null, ResourceRegen? regen = null)
    {
        var set = new ConfigSet();
        Add(set, stats ?? StatRows());
        Add(set, new[] { damage ?? Damage() });
        Add(set, ranks ?? RankRows());
        Add(set, elements ?? ElementRows());
        Add(set, new[] { starting ?? Starting() });
        Add(set, new[] { regen ?? Regen() });
        return set;
    }

    public static void Add<T>(ConfigSet set, IEnumerable<T> rows) where T : class =>
        set.Add(new ConfigTable(typeof(T), TableInfo.For(typeof(T)).Version, rows.Cast<object>().ToList()));

    public static CombatConfig Build(ConfigSet? set = null)
    {
        var errors = new List<ValidationError>();
        var config = CombatConfig.TryBuild(set ?? Set(), errors);
        Assert.True(config != null, string.Join("\n", errors));
        return config!;
    }

    /// <summary>A plain input: 100 power at x1, no defense, rank F against F, no element, no crit bonus.</summary>
    public static DamageInput Input(int power = 100, int multiplierPercent = 100, int defense = 0) => new()
    {
        Power = power,
        SkillMultiplierPercent = multiplierPercent,
        Defense = defense,
        AttackerRank = Rank.F,
        DefenderRank = Rank.F,
        Element = DamageCalc.NoElement,
    };
}
