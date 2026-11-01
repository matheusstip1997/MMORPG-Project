using System.Collections.Concurrent;
using BotClient.Loop.Steps;
using BotClient.World;
using Shared.Data.Tables;

namespace BotClient.Loop;

/// <summary>
/// The loop steps and own readers a run knows (WP-M2-12): one step per id of <see cref="BotBehaviour.LoopStepIds"/>, each
/// in its own file under <c>Loop/Steps</c>, and the readers that fill <see cref="BotOwnState"/>. One per run: it also
/// remembers what was logged once (a placeholder step logs "not available" once per run, not per bot and round).
/// A step is created with its entry's argument (<c>"buy:m3.pickaxeT1"</c>: <c>"m3.pickaxeT1"</c>; empty without one,
/// WP-M3-12); a step that takes none ignores it.
/// </summary>
public sealed class BotStepRegistry
{
    private readonly IReadOnlyDictionary<string, Func<string, IBotStep>> _steps;
    private readonly Func<IReadOnlyList<IBotOwnReader>> _ownReaders;
    private readonly ConcurrentDictionary<string, byte> _logged = new(StringComparer.Ordinal);

    public BotStepRegistry(IReadOnlyDictionary<string, Func<string, IBotStep>> steps, Func<IReadOnlyList<IBotOwnReader>> ownReaders)
    {
        _steps = steps;
        _ownReaders = ownReaders;
    }

    /// <summary>The steps of the M2 and M3 loops. A WP that makes one of them real changes only that step's file.</summary>
    public static BotStepRegistry Default() => new(new Dictionary<string, Func<string, IBotStep>>(StringComparer.Ordinal)
    {
        ["fight"] = argument => new FightStep(argument),
        ["pickup"] = _ => new PickupStep(),
        ["sell"] = _ => new SellStep(),
        ["buy"] = argument => new BuyStep(argument),
        ["run"] = _ => new RunStep(),
        ["learn"] = _ => new LearnStep(),
        ["allocate"] = _ => new AllocateStep(),
        ["rankUp"] = _ => new RankUpStep(),
        ["equip"] = argument => new EquipStep(argument),
        // M3 (WP-M3-12): level plays; the others are placeholders their WPs fill in (Loop/Steps/README.md).
        ["level"] = argument => new LevelStep(argument),
        ["takeJob"] = argument => new TakeJobStep(argument),
        ["gather"] = argument => new GatherStep(argument),
        ["refine"] = argument => new RefineStep(argument),
        ["convert"] = argument => new ConvertStep(argument),
        ["craft"] = argument => new CraftStep(argument),
        ["enchant"] = argument => new EnchantStep(argument),
        ["socket"] = argument => new SocketStep(argument),
        ["useItem"] = argument => new UseItemStep(argument),
        ["learnJob"] = argument => new LearnJobStep(argument), // WP-M3-7
    }, () => [new OwnProgressReader(), new OwnGoldReader()]);

    public IEnumerable<string> Ids => _steps.Keys;

    /// <summary>The <c>loopSteps</c> of <paramref name="behaviour"/> as a loop (a script with <c>loopOnce</c>), one fresh step object per entry.</summary>
    public BotLoop NewLoop(BotBehaviour behaviour)
    {
        var steps = new List<IBotStep>();
        foreach (var entry in behaviour.LoopSteps)
        {
            if (!BotBehaviour.TryParseLoopStep(entry, out string id, out string argument) || !_steps.TryGetValue(id, out var create))
                throw new ArgumentException($"BotBehaviour '{behaviour.Id}': no loop step '{entry}' ({string.Join(", ", _steps.Keys)})");
            steps.Add(create(argument));
        }
        if (steps.Count == 0)
            throw new ArgumentException($"BotBehaviour '{behaviour.Id}' has no loopSteps.");
        return new BotLoop(steps, behaviour.LoopStepTimeoutMs, behaviour.LoopSteps, behaviour.LoopOnce, behaviour.LoopMaxDeathsPerEntry);
    }

    /// <summary>The readers of one bot's own state.</summary>
    public IReadOnlyList<IBotOwnReader> NewOwnReaders() => _ownReaders();

    /// <summary>True the first time <paramref name="key"/> is asked in this run (thread safe).</summary>
    public bool FirstTime(string key) => _logged.TryAdd(key, 0);
}
