using BotClient.Brain;

namespace BotClient.Loop.Steps;

/// <summary>
/// The loop's fight step: the M1 brain (<see cref="BotBrainMode.All"/>: nearest monster, class kit, dodges, the attack chain
/// of R-3.1-10, back off when hurt, walk home when nothing is around), which loots its own kill before it fights on (the
/// drop is locked to the killer for a while, R-4.3-02), so a round of several kills leaves no drop behind (WP-M2-12). Done
/// after <c>BotBehaviour.loopKillsPerRound</c> kills of its own since the step began (the server's Hit messages); the
/// pickup step then takes the last kill's drops. An argument sets the kills of that entry instead (<c>fight:10</c>, a
/// script's gold fight, WP-M3-12).
/// </summary>
public sealed class FightStep : IBotStep
{
    private readonly int _kills;
    private long _killsAtBegin;

    public FightStep(string argument = "")
    {
        _kills = argument.Length == 0 ? 0
            : int.TryParse(argument, out int kills) && kills > 0 ? kills
            : throw new ArgumentException($"fight step: '{argument}' is not a kill count");
    }

    public string Id => "fight";

    public void Begin(BotStepContext context) => _killsAtBegin = context.Counters.Kills;

    public BotStepStatus Tick(BotStepContext context, ref BotCommand command)
    {
        if (context.Counters.Kills - _killsAtBegin >= (_kills > 0 ? _kills : context.Behaviour.LoopKillsPerRound))
            return BotStepStatus.Done;
        command = context.Brain.Think(context.View, BotBrainMode.All);
        return BotStepStatus.Running;
    }
}
