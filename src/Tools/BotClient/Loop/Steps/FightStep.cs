using BotClient.Brain;
using Shared.Data.Tables;

namespace BotClient.Loop.Steps;

/// <summary>
/// The loop's fight step: the M1 brain (<see cref="BotBrainMode.All"/>: nearest monster, class kit, dodges, the attack chain
/// of R-3.1-10, back off when hurt, walk home when nothing is around), which loots its own kill before it fights on (the
/// drop is locked to the killer for a while, R-4.3-02), so a round of several kills leaves no drop behind (WP-M2-12). Done
/// after <c>BotBehaviour.loopKillsPerRound</c> kills of its own since the step began (the server's Hit messages); the
/// pickup step then takes the last kill's drops. An argument sets the kills of that entry instead (<c>fight:10</c>, a
/// script's gold fight, WP-M3-12). <c>fight:&lt;kills&gt;@&lt;spawn marker&gt;</c> fights at that camp for the entry, its home set back
/// after (<c>fight:1@m1.spawn.combat.boss</c>: the exit Blacksmith kills the cyclops for a hide grip, decision 6, WP-M3-6),
/// and counts only kills of the camp's own monster (its <c>SpawnPoints</c> row): a monster that wandered near the camp is
/// fought but does not end the entry (wave 1 QA fix run: an alien's kill ended the boss entry, and no hide came).
/// </summary>
public sealed class FightStep : IBotStep
{
    private readonly int _kills;
    private readonly string _camp;
    private long _killsAtBegin;
    private int _campDefId = -1;
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
        _homeBefore = null;
        _campDefId = -1;
        if (_camp.Length > 0 && context.Data.Markers.TryGetValue(_camp, out var camp))
        {
            _homeBefore = context.Brain.Home;
            context.Brain.SetHome(camp);
            if (context.Data.Rows<SpawnPoints>().FirstOrDefault(s => s.MarkerId == _camp) is { } spawn
                && context.Data.Rows<MonsterDef>().FirstOrDefault(m => m.Id == spawn.MonsterId) is { } monster)
                _campDefId = monster.NetId;
        }
        _killsAtBegin = Kills(context);
    }

    public BotStepStatus Tick(BotStepContext context, ref BotCommand command)
    {
        if (Kills(context) - _killsAtBegin >= (_kills > 0 ? _kills : context.Behaviour.LoopKillsPerRound))
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

    /// <summary>Own kills so far: of the camp's monster for a camp entry with a spawn row, else of any monster.</summary>
    private long Kills(BotStepContext context) =>
        _campDefId >= 0 ? context.Counters.KillsOf(_campDefId) : context.Counters.Kills;
}
