using BotClient.Brain;

namespace BotClient.Loop.Steps;

/// <summary>
/// The loop's fight step: the M1 brain's fight (<see cref="BotBrainMode.Fight"/>: nearest monster, class kit, dodges,
/// the attack chain of R-3.1-10, back off when hurt, walk home when nothing is around), without looting. Done after
/// <c>BotBehaviour.loopKillsPerRound</c> kills of its own since the step began (the server's Hit messages).
/// </summary>
public sealed class FightStep : IBotStep
{
    private long _killsAtBegin;

    public string Id => "fight";

    public void Begin(BotStepContext context) => _killsAtBegin = context.Counters.Kills;

    public BotStepStatus Tick(BotStepContext context, ref BotCommand command)
    {
        if (context.Counters.Kills - _killsAtBegin >= context.Behaviour.LoopKillsPerRound)
            return BotStepStatus.Done;
        command = context.Brain.Think(context.View, BotBrainMode.Fight);
        return BotStepStatus.Running;
    }
}
