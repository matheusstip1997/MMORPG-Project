using BotClient.Brain;
using Shared.Protocol;

namespace BotClient.Loop;

/// <summary>How one entry of a script ended (WP-M3-12): <see cref="Pending"/> until it played.</summary>
public enum StepOutcome : byte
{
    Pending,
    Done,
    /// <summary><see cref="BotStepStatus.NotAvailable"/>: the game or its content has not landed yet.</summary>
    NotYet,
    Failed,
    TimedOut,
}

/// <summary>One entry of a script and its outcome; <see cref="Reason"/> is the not-yet or failure reason (empty when the step gave none).</summary>
public sealed record StepResult(string Entry, StepOutcome Outcome, string Reason);

/// <summary>
/// One bot's loop (WP-M2-12, spec 11.5): its behaviour's <c>loopSteps</c> in order, round after round. A step that ends
/// (<see cref="BotStepStatus.Done"/>, <see cref="BotStepStatus.NotAvailable"/>, <see cref="BotStepStatus.Failed"/>) hands
/// over to the next in the same tick, at most once around the list per tick, so placeholders cost no ticks; a step still
/// running after <c>loopStepTimeoutMs</c> is ended and counted as timed out. Counts per step and status
/// (<c>fight.done</c>, <c>sell.notAvailable</c>, <c>pickup.timedOut</c>, …) and the rounds.
/// <para>
/// A script (<c>loopOnce</c>, WP-M3-12, the M3 exit run) plays the entries once and keeps each one's outcome
/// (<see cref="Outcomes"/>); after the last one it is <see cref="Finished"/> and the bot holds still.
/// </para>
/// </summary>
public sealed class BotLoop
{
    private readonly IBotStep[] _steps;
    private readonly string[] _entries;
    private readonly string[][] _keys;
    private readonly int _stepTimeoutMs;
    private readonly bool _once;
    private readonly StepResult[] _outcomes;
    private int _index = -1;
    private readonly int _maxDeaths;
    private long _startedAtMs;
    private long _deathsAtStart;

    /// <param name="maxDeathsPerEntry">A script entry fails once the bot died more than this often in it; 0: no limit.</param>
    public BotLoop(IReadOnlyList<IBotStep> steps, int stepTimeoutMs, IReadOnlyList<string>? entries = null, bool once = false,
        int maxDeathsPerEntry = 0)
    {
        _maxDeaths = maxDeathsPerEntry;
        _steps = steps.ToArray();
        _entries = entries?.ToArray() ?? _steps.Select(s => s.Id).ToArray();
        if (_entries.Length != _steps.Length)
            throw new ArgumentException("one entry per step");
        _stepTimeoutMs = stepTimeoutMs;
        _once = once;
        _keys = _steps.Select(s => new[] { s.Id + ".done", s.Id + ".notAvailable", s.Id + ".failed", s.Id + ".timedOut" }).ToArray();
        _outcomes = _entries.Select(e => new StepResult(e, StepOutcome.Pending, "")).ToArray();
    }

    public IReadOnlyList<IBotStep> Steps => _steps;

    /// <summary>The current step, null before the first tick and after a script finished.</summary>
    public IBotStep? Current => _index >= 0 && !Finished ? _steps[_index] : null;

    /// <summary>A script that played its last entry (a loop never finishes).</summary>
    public bool Finished { get; private set; }

    public bool IsScript => _once;

    /// <summary>Each entry's outcome (a script's; a loop keeps the newest outcome of each entry).</summary>
    public IReadOnlyList<StepResult> Outcomes => _outcomes;

    public BotCommand Tick(BotStepContext context)
    {
        if (Finished)
            return context.Brain.Hold();
        long now = context.NowMs;
        if (_index < 0)
            Enter(0, context, now);
        for (int pass = 0; pass < _steps.Length; pass++)
        {
            var step = _steps[_index];
            var command = context.Brain.Hold();
            context.LastReason = "";
            var status = step.Tick(context, ref command);
            StepOutcome outcome;
            long deaths = context.Counters.Deaths - _deathsAtStart;
            if (status == BotStepStatus.Running && _once && _maxDeaths > 0 && deaths > _maxDeaths)
            {
                // BotBehaviour.loopMaxDeathsPerEntry: fail with the deaths instead of running out the run's clock.
                context.Counters.AddLoop(_keys[_index][2]);
                outcome = StepOutcome.Failed;
                context.LastReason = $"died {deaths} times in this entry (loopMaxDeathsPerEntry {_maxDeaths})";
            }
            else if (status == BotStepStatus.Running)
            {
                if (now - _startedAtMs <= _stepTimeoutMs)
                    return command;
                context.Counters.AddLoop(_keys[_index][3]);
                outcome = StepOutcome.TimedOut;
            }
            else
            {
                context.Counters.AddLoop(_keys[_index][status == BotStepStatus.Done ? 0 : status == BotStepStatus.NotAvailable ? 1 : 2]);
                outcome = status == BotStepStatus.Done ? StepOutcome.Done : status == BotStepStatus.NotAvailable ? StepOutcome.NotYet : StepOutcome.Failed;
            }
            _outcomes[_index] = new StepResult(_entries[_index], outcome, outcome is StepOutcome.NotYet or StepOutcome.Failed ? context.LastReason : "");
            int next = _index + 1;
            if (next == _steps.Length)
            {
                next = 0;
                context.Counters.LoopRounds++;
                if (_once)
                {
                    Finished = true;
                    return context.Brain.Hold();
                }
            }
            Enter(next, context, now);
        }
        return context.Brain.Hold(); // once around the list: the next tick goes on
    }

    public void OnReliable(BotStepContext context, ReliableMessageId id, ReadOnlySpan<byte> message)
    {
        foreach (var step in _steps)
            step.OnReliable(context, id, message);
    }

    private void Enter(int index, BotStepContext context, long now)
    {
        _index = index;
        _startedAtMs = now;
        _deathsAtStart = context.Counters.Deaths;
        _steps[index].Begin(context);
    }
}
