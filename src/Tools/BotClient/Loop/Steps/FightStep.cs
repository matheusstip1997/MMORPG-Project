using BotClient.Brain;
using Shared.Data.Tables;
using Shared.Protocol;

namespace BotClient.Loop.Steps;

/// <summary>
/// The loop's fight step: the M1 brain (<see cref="BotBrainMode.All"/>: nearest monster, class kit, dodges, the attack chain
/// of R-3.1-10, back off when hurt, walk home when nothing is around), which loots its own kill before it fights on (the
/// drop is locked to the killer for a while, R-4.3-02), so a round of several kills leaves no drop behind (WP-M2-12). Done
/// after <c>BotBehaviour.loopKillsPerRound</c> kills of its own since the step began (the server's Hit messages); the
/// pickup step then takes the last kill's drops. An argument sets the kills of that entry instead (<c>fight:10</c>, a
/// script's gold fight, WP-M3-12). <c>fight:&lt;kills&gt;@&lt;spawn marker&gt;</c> fights at that camp for the entry, its home set back
/// after (<c>fight:14@field1.spawn.wolf.c1</c>: the exit Blacksmith kills wolves for a sinew grip, WP-M3-6).
/// <c>fight:until=&lt;item id&gt;[x&lt;count&gt;]@&lt;spawn marker&gt;</c> fights until the bag holds that many of the item (default 1;
/// the exit Blacksmith farms enchant stones, a rare drop, WP-M3-8 user answer); the script's step timeout bounds it.
/// A camp entry with a kill count counts only kills of the camp's own monster (its <c>SpawnPoints</c> row): a monster that
/// wandered near the camp is fought but does not end the entry (wave 1 QA fix run: an alien's kill ended the boss entry, and
/// no hide came).
/// </summary>
public sealed class FightStep : IBotStep
{
    private const string UntilPrefix = "until=";

    private readonly int _kills;
    private readonly string _camp;
    private readonly string _untilItem = string.Empty;
    private readonly int _untilCount;
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
        if (argument.StartsWith(UntilPrefix, StringComparison.Ordinal))
        {
            (_untilItem, _untilCount) = ParseUntil(argument.Substring(UntilPrefix.Length));
            return;
        }
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
        bool done = _untilItem.Length > 0
            ? Held(context, _untilItem) >= _untilCount
            : Kills(context) - _killsAtBegin >= (_kills > 0 ? _kills : context.Behaviour.LoopKillsPerRound);
        if (done)
        {
            if (_homeBefore is { } home)
                context.Brain.SetHome(home); // back to the camp of its level
            _homeBefore = null;
            return BotStepStatus.Done;
        }
        if (_camp.Length > 0 && !context.Data.Markers.ContainsKey(_camp))
            return context.NotAvailable(Id, $"no spawn marker '{_camp}'");
        if (_untilItem.Length > 0 && !context.Data.Rows<ItemDef>().Any(i => i.Id == _untilItem))
            return context.NotAvailable(Id, $"no item '{_untilItem}'");
        command = context.Brain.Think(context.View, BotBrainMode.All);
        return BotStepStatus.Running;
    }

    /// <summary>Own kills so far: of the camp's monster for a camp entry with a spawn row, else of any monster.</summary>
    private long Kills(BotStepContext context) =>
        _campDefId >= 0 ? context.Counters.KillsOf(_campDefId) : context.Counters.Kills;

    /// <summary><c>&lt;item id&gt;[x&lt;count&gt;]</c>: the item and how many the bag must hold.</summary>
    private static (string Item, int Count) ParseUntil(string text)
    {
        int x = text.LastIndexOf('x');
        if (x > 0 && x < text.Length - 1 && int.TryParse(text.Substring(x + 1), out int count))
        {
            if (count <= 0)
                throw new ArgumentException($"fight step: '{text}' needs a count above 0");
            return (text.Substring(0, x), count);
        }
        if (text.Length == 0)
            throw new ArgumentException("fight step: until= names no item");
        return (text, 1);
    }

    /// <summary>Units of <paramref name="itemId"/> in every container of the committed contents.</summary>
    private static int Held(BotStepContext context, string itemId)
    {
        var contents = context.Inventory.Contents;
        if (contents == null)
            return 0;
        int held = 0;
        foreach (var entry in contents.Slots)
            held += Count(context, entry, itemId);
        foreach (var container in contents.Containers)
        {
            foreach (var entry in container.Slots)
                held += Count(context, entry, itemId);
        }
        return held;
    }

    private static int Count(BotStepContext context, InventorySlotEntry entry, string itemId) =>
        context.Item(entry.ItemNetId)?.Id == itemId ? entry.Count : 0;
}
