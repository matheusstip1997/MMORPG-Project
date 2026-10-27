using BotClient.Brain;
using Shared.Protocol;

namespace BotClient.Loop;

/// <summary>
/// One bot's M2 loop (WP-M2-12, spec 11.5): its behaviour's <c>loopSteps</c> in order, round after round. A step that ends
/// (<see cref="BotStepStatus.Done"/>, <see cref="BotStepStatus.NotAvailable"/>, <see cref="BotStepStatus.Failed"/>) hands
/// over to the next in the same tick, at most once around the list per tick, so placeholders cost no ticks; a step still
/// running after <c>loopStepTimeoutMs</c> is ended and counted as timed out. Counts per step and status
/// (<c>fight.done</c>, <c>sell.notAvailable</c>, <c>pickup.timedOut</c>, …) and the rounds.
/// </summary>
public sealed class BotLoop
{
    private readonly IBotStep[] _steps;
    private readonly string[][] _keys;
    private readonly int _stepTimeoutMs;
    private int _index = -1;
    private long _startedAtMs;

    public BotLoop(IReadOnlyList<IBotStep> steps, int stepTimeoutMs)
    {
        _steps = steps.ToArray();
        _stepTimeoutMs = stepTimeoutMs;
        _keys = _steps.Select(s => new[] { s.Id + ".done", s.Id + ".notAvailable", s.Id + ".failed", s.Id + ".timedOut" }).ToArray();
    }

    public IReadOnlyList<IBotStep> Steps => _steps;

    /// <summary>The current step, null before the first tick.</summary>
    public IBotStep? Current => _index >= 0 ? _steps[_index] : null;

    public BotCommand Tick(BotStepContext context)
    {
        long now = context.NowMs;
        if (_index < 0)
            Enter(0, context, now);
        for (int pass = 0; pass < _steps.Length; pass++)
        {
            var step = _steps[_index];
            var command = context.Brain.Hold();
            var status = step.Tick(context, ref command);
            if (status == BotStepStatus.Running)
            {
                if (now - _startedAtMs <= _stepTimeoutMs)
                    return command;
                context.Counters.AddLoop(_keys[_index][3]);
            }
            else
            {
                context.Counters.AddLoop(_keys[_index][status == BotStepStatus.Done ? 0 : status == BotStepStatus.NotAvailable ? 1 : 2]);
            }
            int next = _index + 1;
            if (next == _steps.Length)
            {
                next = 0;
                context.Counters.LoopRounds++;
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
        _steps[index].Begin(context);
    }
}
