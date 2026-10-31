using BotClient.Brain;

namespace BotClient.Loop.Steps;

/// <summary>
/// The loop's fight step: the M1 brain (<see cref="BotBrainMode.All"/>: nearest monster, class kit, dodges, the attack chain
/// of R-3.1-10, back off when hurt, walk home when nothing is around), which loots its own kill before it fights on (the
/// drop is locked to the killer for a while, R-4.3-02), so a round of several kills leaves no drop behind (WP-M2-12). Done
/// after <c>BotBehaviour.loopKillsPerRound</c> kills of its own since the step began (the server's Hit messages); the
/// pickup step then takes the last kill's drops. An argument sets the kills of that entry instead (<c>fight:10</c>, a
/// script's gold fight, WP-M3-12). <c>fight:&lt;kills&gt;@&lt;spawn marker&gt;</c> fights at that camp for the entry, its home set back
/// after (<c>fight:1@m1.spawn.combat.boss</c>: the exit Blacksmith kills the cyclops for a hide grip, decision 6, WP-M3-6).
/// </summary>
public sealed class FightStep : IBotStep
{
    private readonly int _kills;
    private readonly string _camp;
    private long _killsAtBegin;
    private Shared.Simulation.FixVec2? _homeBefore;

    public FightStep(string argument = "")
    {
        int at = argument.IndexOf('@');
        _camp = at >= 0 ? argument.Substring(at + 1) : string.Empty;
        if (at >= 0 && _camp.Length == 0)
            throw new ArgumentException($"fight step: '{argument}' names no spawn marker after @");
        argument = at >= 0 ? argument.Substring(0, at) : argument;
        _kills = argument.Length == 0 ? 0
            : int.TryParse(argument, out int kills) && kills > 0 ? kills
            : throw new ArgumentException($"fight step: '{argument}' is not a kill count");
    }

    public string Id => "fight";

    public void Begin(BotStepContext context)
    {
        _killsAtBegin = context.Counters.Kills;
        _homeBefore = null;
        if (_camp.Length > 0 && context.Data.Markers.TryGetValue(_camp, out var camp))
        {
            _homeBefore = context.Brain.Home;
            context.Brain.SetHome(camp);
        }
    }

    public BotStepStatus Tick(BotStepContext context, ref BotCommand command)
    {
        if (context.Counters.Kills - _killsAtBegin >= (_kills > 0 ? _kills : context.Behaviour.LoopKillsPerRound))
        {
            if (_homeBefore is { } home)
                context.Brain.SetHome(home); // back to the camp of its level
            _homeBefore = null;
            return BotStepStatus.Done;
        }
        if (_camp.Length > 0 && !context.Data.Markers.ContainsKey(_camp))
            return context.NotAvailable(Id, $"no spawn marker '{_camp}'");
        command = context.Brain.Think(context.View, BotBrainMode.All);
        return BotStepStatus.Running;
    }
}
