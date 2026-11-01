using System.Globalization;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Simulation.Crafting;
using Shared.Simulation.Jobs;
using Shared.Util;

namespace EconomySimulator;

/// <summary>Attempts and stones from +0 to one target level over every trial (spec 12.5, WP-M3-8).</summary>
public sealed record EnchantTarget(int Level, double MeanAttempts, int P50Attempts, int P95Attempts, int MaxAttempts, double MeanStones);

/// <summary>One ladder run: the job tree bonus it used (percentage points), the stones per try and the targets +1 to +9.</summary>
public sealed record EnchantLadder(int BonusPoints, int StonesPerTry, int Trials, int Unfinished, IReadOnlyList<EnchantTarget> Targets);

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
        var ladders = new List<EnchantLadder> { Ladder(odds, 0, stones, seed) };
        if (cap > 0)
            ladders.Add(Ladder(odds, cap, stones, seed));
        return ladders;
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
                output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"tree bonus {ladder.BonusPoints} pp, {ladder.StonesPerTry} stone(s) per try (tier 1), {ladder.Trials} trials:"));
                output.WriteLine("  target  mean tries  p50  p95   max  mean stones");
                foreach (var t in ladder.Targets)
                    output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"  +{t.Level,-5}  {t.MeanAttempts,10:0.0}  {t.P50Attempts,3}  {t.P95Attempts,3}  {t.MaxAttempts,4}  {t.MeanStones,11:0.0}"));
            }
        }
        output.WriteLine($"[{(verdict.Pass ? "PASS" : "FAIL")}] {verdict.Name}: {verdict.Detail}");
    }

    private static int Percentile(List<int> sorted, int percent) =>
        sorted[Math.Clamp((int)Math.Ceiling(sorted.Count * percent / 100.0) - 1, 0, sorted.Count - 1)];
}
