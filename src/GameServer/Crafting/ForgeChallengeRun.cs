using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Crafting;

namespace GameServer.Crafting;

/// <summary>
/// A session's rounds of the timing bar (spec 5.3 M3 decision 12; WP-M3-10), on <see cref="ForgeSession.Challenge"/>: fixed
/// arrays of <see cref="ForgeStations.MaxSessionActions"/> rounds made once when the session starts, so nothing is allocated
/// later and nothing runs per tick (rounds close when the next action, a press or the finish comes). World thread only.
/// </summary>
public sealed class ForgeChallengeRun
{
    private readonly TimingBarParams[] _bars = new TimingBarParams[ForgeStations.MaxSessionActions];
    private readonly long[] _issuedAtMs = new long[ForgeStations.MaxSessionActions];
    private readonly int[] _scores = new int[ForgeStations.MaxSessionActions];
    private readonly ForgeHitOutcome[] _outcomes = new ForgeHitOutcome[ForgeStations.MaxSessionActions];

    public ForgeChallengeRun(ForgeChallengeDef row, int actionNetId)
    {
        Row = row;
        ActionNetId = actionNetId;
    }

    public ForgeChallengeDef Row { get; }

    /// <summary>The forge action that opens a round (the Blacksmith's hit).</summary>
    public int ActionNetId { get; }

    /// <summary>Rounds opened so far.</summary>
    public int Count { get; private set; }

    /// <summary>The open round's number (from 1), 0 when none is open.</summary>
    public int Open { get; private set; }

    public TimingBarParams Bar(int round) => _bars[round - 1];

    public long IssuedAtMs(int round) => _issuedAtMs[round - 1];

    public int ScoreOf(int round) => _scores[round - 1];

    public ForgeHitOutcome OutcomeOf(int round) => _outcomes[round - 1];

    /// <summary>The sum of the closed rounds' scores.</summary>
    public int ClosedScore
    {
        get
        {
            long sum = 0;
            for (int i = 0; i < Count; i++)
                sum += _scores[i];
            return sum > int.MaxValue ? int.MaxValue : (int)sum;
        }
    }

    /// <summary>Rounds closed so far (every opened round but the open one).</summary>
    public int ClosedCount => Open == 0 ? Count : Count - 1;

    /// <summary>Opens the next round; false when the session has used every round it has room for.</summary>
    public bool TryOpen(in TimingBarParams bar, long nowMs)
    {
        if (Count == _bars.Length)
            return false;
        _bars[Count] = bar;
        _issuedAtMs[Count] = nowMs;
        _scores[Count] = 0;
        _outcomes[Count] = ForgeHitOutcome.None;
        Count++;
        Open = Count;
        return true;
    }

    /// <summary>Closes the open round with its score and outcome.</summary>
    public void CloseOpen(int score, ForgeHitOutcome outcome)
    {
        if (Open == 0)
            return;
        _scores[Open - 1] = score;
        _outcomes[Open - 1] = outcome;
        Open = 0;
    }
}
