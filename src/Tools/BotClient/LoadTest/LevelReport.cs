using System.Globalization;
using System.Text;

namespace BotClient.LoadTest;

/// <summary>One sample of the level report: seconds since play began, each bot's level (null: unknown).</summary>
public sealed record LevelSample(long AtSeconds, int?[] Levels);

/// <summary>
/// The M2 exit report (WP-M2-12, spec 12.2 "a new character plays from level 1 to about 30"): each bot's level over the
/// run, sampled every <c>BotScenario.levelSampleMs</c>, when each first reached the target level, and the level criterion.
/// The level comes from the bot's own client state (<c>BotOwnState</c>); while the protocol carries none, the report says
/// so and the criterion fails. Pure: the load test feeds it.
/// </summary>
public sealed class LevelReport
{
    public const string NotInProtocol = "level not in protocol yet";

    private readonly List<LevelSample> _samples = [];
    private readonly int?[] _final;
    private readonly long?[] _reachedAtSeconds;

    public LevelReport(int bots, int targetLevel, bool levelInProtocol)
    {
        Bots = bots;
        TargetLevel = targetLevel;
        LevelInProtocol = levelInProtocol;
        _final = new int?[bots];
        _reachedAtSeconds = new long?[bots];
    }

    public int Bots { get; }

    /// <summary>0: no level criterion (the report still prints).</summary>
    public int TargetLevel { get; }

    /// <summary>The bot client can read a level from the protocol at all.</summary>
    public bool LevelInProtocol { get; }

    public IReadOnlyList<LevelSample> Samples => _samples;

    public IReadOnlyList<int?> Final => _final;

    /// <summary>Seconds after play began at which each bot was first seen at the target level; null: not reached.</summary>
    public IReadOnlyList<long?> ReachedAtSeconds => _reachedAtSeconds;

    /// <summary>Some bot's level was ever known.</summary>
    public bool AnyLevel => _final.Any(l => l.HasValue);

    public int Reached => _reachedAtSeconds.Count(r => r.HasValue);

    public bool AllReached => TargetLevel > 0 && Bots > 0 && Reached == Bots;

    /// <summary>The bots' levels now: updates the final levels and the time reached; <paramref name="keep"/> adds a sample.</summary>
    public void Observe(long elapsedMs, IReadOnlyList<int?> levels, bool keep)
    {
        long atSeconds = elapsedMs / 1000;
        for (int i = 0; i < Bots && i < levels.Count; i++)
        {
            if (levels[i] is not int level)
                continue;
            _final[i] = level;
            if (TargetLevel > 0 && level >= TargetLevel && !_reachedAtSeconds[i].HasValue)
                _reachedAtSeconds[i] = atSeconds;
        }
        if (keep)
            _samples.Add(new LevelSample(atSeconds, levels.Take(Bots).ToArray()));
    }

    /// <summary>The pass criterion: every bot at the target level within <paramref name="durationMs"/>.</summary>
    public Criterion Criterion(int durationMs)
    {
        string expected = $"{Bots} of {Bots} within {durationMs / 1000} s";
        string actual = !AnyLevel
            ? (LevelInProtocol ? "no bot received a level" : NotInProtocol)
            : $"{Reached} of {Bots} (levels {string.Join(", ", _final.Select(Text))})";
        return new Criterion($"bots reached level {TargetLevel}", expected, actual, AllReached);
    }

    /// <summary>The printed report: a table of levels per bot over time, then who reached the target and when.</summary>
    public IEnumerable<string> Lines()
    {
        if (!AnyLevel)
        {
            yield return LevelInProtocol
                ? "level per bot: no bot received a level"
                : $"level per bot: {NotInProtocol} (no own-entity level message; WP-M2-3)";
            yield break;
        }
        var header = new StringBuilder("level per bot:   time");
        for (int i = 0; i < Bots; i++)
            header.Append(CultureInfo.InvariantCulture, $" {"bot" + i,5}");
        yield return header.ToString();
        foreach (var sample in _samples)
        {
            var row = new StringBuilder($"               {sample.AtSeconds,5} s");
            foreach (var level in sample.Levels)
                row.Append(CultureInfo.InvariantCulture, $" {Text(level),5}");
            yield return row.ToString();
        }
        if (TargetLevel > 0)
        {
            yield return $"level {TargetLevel}: " + string.Join(", ", Enumerable.Range(0, Bots).Select(i =>
                _reachedAtSeconds[i] is long at ? $"bot{i} at {at} s" : $"bot{i} not reached (level {Text(_final[i])})"));
        }
    }

    /// <summary>For summary.json.</summary>
    public object Summary(int sampleMs) => new
    {
        targetLevel = TargetLevel, sampleMs, levelInProtocol = LevelInProtocol, anyLevel = AnyLevel, reached = Reached,
        final = _final, reachedAtSeconds = _reachedAtSeconds,
        samples = _samples.Select(s => new { atSeconds = s.AtSeconds, levels = s.Levels }),
    };

    private static string Text(int? level) => level?.ToString(CultureInfo.InvariantCulture) ?? "?";
}
