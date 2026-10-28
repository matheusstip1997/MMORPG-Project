namespace EconomySimulator;

/// <summary>
/// The structural checks of spec 12.5 ("a sink that can never balance a faucet, a loop with unbounded growth") over sampled
/// series, as pure functions. Each reads the last window of a series: <see cref="WindowStart"/> gives where it begins.
/// </summary>
public static class SettleCheck
{
    /// <summary>The first index of the last <paramref name="windowPercent"/> of <paramref name="count"/> samples (at least one sample).</summary>
    public static int WindowStart(int count, int windowPercent)
    {
        int length = Math.Max(1, (int)Math.Ceiling(count * windowPercent / 100.0));
        return Math.Max(0, count - length);
    }

    /// <summary>How far a multiplier moves inside the window: max minus min, in its own unit (percentage points).</summary>
    public static double Spread(IReadOnlyList<double> samples, int from)
    {
        if (from >= samples.Count)
            return 0;
        double min = double.MaxValue, max = double.MinValue;
        for (int i = from; i < samples.Count; i++)
        {
            min = Math.Min(min, samples[i]);
            max = Math.Max(max, samples[i]);
        }
        return max - min;
    }

    /// <summary>
    /// True when a counter still grows through the window: the mean of the window's second half is above the first half's
    /// by more than <paramref name="tolerancePercent"/> plus <paramref name="slack"/> (one unit, so a counter near zero does
    /// not trip on noise). A settled counter wanders around its level; an unbounded one (no decay) climbs.
    /// </summary>
    public static bool Grows(IReadOnlyList<double> samples, int from, int tolerancePercent, double slack)
    {
        int length = samples.Count - from;
        if (length < 2)
            return false;
        int half = length / 2;
        double first = 0, second = 0;
        for (int i = 0; i < half; i++)
            first += samples[from + i];
        for (int i = length - half; i < length; i++)
            second += samples[from + i];
        first /= half;
        second /= half;
        return second > first * (100 + tolerancePercent) / 100.0 + slack;
    }

    /// <summary>The largest distance of a value in the window from the window's mean, in percent of that mean (0 for a zero mean).</summary>
    public static double DeviationPercent(IReadOnlyList<double> samples, int from)
    {
        int length = samples.Count - from;
        if (length <= 0)
            return 0;
        double mean = 0;
        for (int i = from; i < samples.Count; i++)
            mean += samples[i];
        mean /= length;
        if (mean <= 0)
            return 0;
        double widest = 0;
        for (int i = from; i < samples.Count; i++)
            widest = Math.Max(widest, Math.Abs(samples[i] - mean));
        return widest * 100 / mean;
    }
}
