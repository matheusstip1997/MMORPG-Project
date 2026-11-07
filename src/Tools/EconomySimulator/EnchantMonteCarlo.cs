using System.Globalization;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Simulation.Crafting;
using Shared.Simulation.Jobs;
using Shared.Util;

namespace EconomySimulator;

/// <summary>Attempts and stones from +0 to one target level over every trial (spec 12.5, WP-M3-8).</summary>
public sealed record EnchantTarget(int Level, double MeanAttempts, int P50Attempts, int P95Attempts, int MaxAttempts, double MeanStones);

/// <summary>
/// One ladder run: the job tree bonus it used (percentage points), the stones per try and the targets +1 to +9;
/// <see cref="Jobs"/> names the jobs whose tree reaches this bonus, and <see cref="StonesByTier"/> the stones to +9 per item tier.
/// </summary>
public sealed record EnchantLadder(int BonusPoints, int StonesPerTry, int Trials, int Unfinished, IReadOnlyList<EnchantTarget> Targets)
{
    public IReadOnlyList<string> Jobs { get; init; } = [];
    public IReadOnlyList<EnchantStoneTier> StonesByTier { get; init; } = [];
}

/// <summary>An item tier's stones per try (<c>EnchantMaterial</c>) and its mean stones to +9.</summary>
public sealed record EnchantStoneTier(int Tier, int StonesPerTry, double MeanStonesToNine);

/// <summary>
/// The enchant Monte Carlo of the M3 economy (spec 12.5, 10.2; WP-M3-8): seeded trials that enchant one item from +0 until
/// it first reaches +9 with the <c>EnchantOdds</c> weights (the shared <see cref="EnchantMath"/>, as the server rolls them),
/// once without the job tree bonus and once at its <c>JobBonusCap</c>. Every try consumes its stones (R-5.3-04: always), so
/// the stones counted are all an item sink (R-4.2-04). Reports the distribution of attempts and stones to each level; the
/// structural check is that every trial reaches +9 within <see cref="GuardAttempts"/> (a level that cannot rise would never
/// finish). Never tunes a value.
/// </summary>
public static class EnchantMonteCarlo
{
    /// <summary>Trials per ladder (enough for a stable p95; the tool's own sample size, not game data).</summary>
    public const int Trials = 2000;

    /// <summary>A trial still below +9 after this many tries counts as unfinished (the check fails).</summary>
    public const int GuardAttempts = 100_000;

    /// <summary>The <c>Pcg32Rng</c> stream of the trials.</summary>
    private const ulong Stream = 0x454D4343UL; // "EMCC"

    /// <summary>The ladders without and with the capped job tree bonus; empty when the enchant tables are not loaded.</summary>
    public static IReadOnlyList<EnchantLadder> Run(ConfigSet tables, ulong seed)
    {
        var odds = new EnchantOdds[EnchantLimits.MaxLevel];
        if (!tables.TryGet(typeof(EnchantOdds), out var rows))
            return Array.Empty<EnchantLadder>();
        foreach (var row in rows.Rows)
            odds[((EnchantOdds)row).Level] = (EnchantOdds)row;
        if (odds.Any(o => o == null))
            return Array.Empty<EnchantLadder>();
        int stones = SimEconomy.Rows<EnchantMaterial>(tables).FirstOrDefault(m => m.Tier == 1)?.Count ?? 0;
        int cap = SimEconomy.Rows<JobBonusCap>(tables).FirstOrDefault(c => c.Kind == JobBonusKind.EnchantOddsPercent)?.Cap ?? 0;
        // EnchantOdds is one table for every job (R-5.3-04): what differs per job is the bonus its own tree reaches, capped.
        var jobs = ReachableBonusByJob(tables, cap);
        var bonuses = new SortedSet<int>(jobs.Values) { 0 };
        if (cap > 0)
            bonuses.Add(cap);
        var tiers = SimEconomy.Rows<EnchantMaterial>(tables).OrderBy(m => m.Tier).ToList();
        return bonuses.Select(bonus =>
        {
            var ladder = Ladder(odds, bonus, stones, seed);
            double tries = ladder.Targets[^1].MeanAttempts;
            return ladder with
            {
                Jobs = jobs.Where(j => j.Value == bonus).Select(j => j.Key).ToList(),
                StonesByTier = tiers.Select(m => new EnchantStoneTier(m.Tier, m.Count, tries * m.Count)).ToList(),
            };
        }).ToList();
    }

    /// <summary>
    /// The <c>enchantOddsPercent</c> each job's tree reaches: the top value of every such node of the job (<c>JobTree</c>), summed
    /// and capped by <c>JobBonusCap</c> as <c>IJobBonusQuery</c> does. Every <c>JobDef</c> has a row, 0 without such a node.
    /// </summary>
    public static SortedDictionary<string, int> ReachableBonusByJob(ConfigSet tables, int cap)
    {
        var reach = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var job in SimEconomy.Rows<JobDef>(tables))
        {
            int sum = SimEconomy.Rows<JobTree>(tables).Where(n => n.JobId == job.Id && n.BonusKind == JobBonusKind.EnchantOddsPercent)
                .Sum(n => n.ValuePerLevel.Length == 0 ? 0 : n.ValuePerLevel[^1]);
            reach[job.Id] = Math.Min(sum, cap);
        }
        return reach;
    }

    /// <summary>One ladder: <paramref name="trials"/> seeded trials with <paramref name="bonusPoints"/>, each stopped at <paramref name="guard"/> tries.</summary>
    public static EnchantLadder Ladder(IReadOnlyList<EnchantOdds> odds, int bonusPoints, int stonesPerTry, ulong seed, int trials = Trials,
        int guard = GuardAttempts)
    {
        var weights = odds.Select(o => EnchantMath.Weights(o, bonusPoints)).ToArray();
        var reached = new List<int>[EnchantLimits.MaxLevel + 1];
        for (int level = 1; level <= EnchantLimits.MaxLevel; level++)
            reached[level] = new List<int>(trials);
        var rng = new Pcg32Rng(seed, Stream);
        int unfinished = 0;
        for (int trial = 0; trial < trials; trial++)
        {
            int level = 0, best = 0, attempts = 0;
            while (best < EnchantLimits.MaxLevel && attempts < guard)
            {
                attempts++;
                level = EnchantMath.Apply(level, EnchantMath.Outcome(weights[level], rng.NextInt(100)));
                if (level > best)
                {
                    best = level;
                    reached[level].Add(attempts);
                }
            }
            if (best < EnchantLimits.MaxLevel)
                unfinished++;
        }
        var targets = new List<EnchantTarget>();
        for (int level = 1; level <= EnchantLimits.MaxLevel; level++)
        {
            var sorted = reached[level].OrderBy(a => a).ToList();
            if (sorted.Count == 0)
            {
                targets.Add(new EnchantTarget(level, 0, 0, 0, 0, 0));
                continue;
            }
            double mean = sorted.Average();
            targets.Add(new EnchantTarget(level, mean, Percentile(sorted, 50), Percentile(sorted, 95), sorted[^1], mean * stonesPerTry));
        }
        return new EnchantLadder(bonusPoints, stonesPerTry, trials, unfinished, targets);
    }

    /// <summary>The verdict of <c>--check</c>: every trial of every ladder reached +9.</summary>
    public static SimVerdict Check(IReadOnlyList<EnchantLadder> ladders)
    {
        if (ladders.Count == 0)
            return new SimVerdict("enchant ladder bounded", false, false, "no complete EnchantOdds table (levels 0 to 8)");
        int unfinished = ladders.Sum(l => l.Unfinished);
        var nine = ladders.Select(l => l.Targets[^1]).ToList();
        return new SimVerdict("enchant ladder bounded", unfinished == 0, false, unfinished == 0
            ? string.Join("; ", ladders.Select((l, i) => FormattableString.Invariant(
                $"+9 in {nine[i].MeanAttempts:0.#} tries on average (p95 {nine[i].P95Attempts}), {nine[i].MeanStones:0.#} stones sunk, tree bonus {l.BonusPoints} pp")))
            : FormattableString.Invariant($"{unfinished} trial(s) never reached +9 in {GuardAttempts} tries: a level cannot rise"));
    }

    public static void Print(IReadOnlyList<EnchantLadder> ladders, TextWriter output, bool verdictOnly)
    {
        var verdict = Check(ladders);
        output.WriteLine();
        output.WriteLine("== enchant (EnchantOdds, R-5.3-04; every stone is a sink) ==");
        if (!verdictOnly)
        {
            foreach (var ladder in ladders)
            {
                string jobs = ladder.Jobs.Count == 0 ? "" : " (the tree of " + string.Join(", ", ladder.Jobs) + ")";
                output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"tree bonus {ladder.BonusPoints} pp{jobs}, {ladder.StonesPerTry} stone(s) per try (tier 1), {ladder.Trials} trials:"));
                output.WriteLine("  target  mean tries  p50  p95   max  mean stones");
                foreach (var t in ladder.Targets)
                    output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"  +{t.Level,-5}  {t.MeanAttempts,10:0.0}  {t.P50Attempts,3}  {t.P95Attempts,3}  {t.MaxAttempts,4}  {t.MeanStones,11:0.0}"));
                if (ladder.StonesByTier.Count > 0)
                    output.WriteLine("  stones to +9 by item tier (EnchantMaterial): " + string.Join(", ", ladder.StonesByTier.Select(s =>
                        string.Create(CultureInfo.InvariantCulture, $"tier {s.Tier} {s.StonesPerTry} per try, {s.MeanStonesToNine:0.#}"))));
            }
        }
        output.WriteLine($"[{(verdict.Pass ? "PASS" : "FAIL")}] {verdict.Name}: {verdict.Detail}");
    }

    private static int Percentile(List<int> sorted, int percent) =>
        sorted[Math.Clamp((int)Math.Ceiling(sorted.Count * percent / 100.0) - 1, 0, sorted.Count - 1)];
}
