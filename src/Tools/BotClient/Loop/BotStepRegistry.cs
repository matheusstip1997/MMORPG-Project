using System.Collections.Concurrent;
using BotClient.Loop.Steps;
using BotClient.World;
using Shared.Data.Tables;

namespace BotClient.Loop;

/// <summary>
/// The loop steps and own readers a run knows (WP-M2-12): one step per id of <see cref="BotBehaviour.LoopStepIds"/>, each
/// in its own file under <c>Loop/Steps</c>, and the readers that fill <see cref="BotOwnState"/>. One per run: it also
/// remembers what was logged once (a placeholder step logs "not available" once per run, not per bot and round).
/// </summary>
public sealed class BotStepRegistry
{
    private readonly IReadOnlyDictionary<string, Func<IBotStep>> _steps;
    private readonly Func<IReadOnlyList<IBotOwnReader>> _ownReaders;
    private readonly ConcurrentDictionary<string, byte> _logged = new(StringComparer.Ordinal);

    public BotStepRegistry(IReadOnlyDictionary<string, Func<IBotStep>> steps, Func<IReadOnlyList<IBotOwnReader>> ownReaders)
    {
        _steps = steps;
        _ownReaders = ownReaders;
    }

    /// <summary>The steps of the M2 loop. A WP that makes one of them real changes only that step's file.</summary>
    public static BotStepRegistry Default() => new(new Dictionary<string, Func<IBotStep>>(StringComparer.Ordinal)
    {
        ["fight"] = () => new FightStep(),
        ["pickup"] = () => new PickupStep(),
        ["sell"] = () => new SellStep(),
        ["buy"] = () => new BuyStep(),
        ["run"] = () => new RunStep(),
        ["learn"] = () => new LearnStep(),
        ["rankUp"] = () => new RankUpStep(),
        ["equip"] = () => new EquipStep(),
    }, () => [new OwnProgressReader(), new OwnGoldReader()]);

    public IEnumerable<string> Ids => _steps.Keys;

    /// <summary>The <c>loopSteps</c> of <paramref name="behaviour"/> as a loop, one fresh step object per id.</summary>
    public BotLoop NewLoop(BotBehaviour behaviour)
    {
        var steps = new List<IBotStep>();
        foreach (var id in behaviour.LoopSteps)
        {
            if (!_steps.TryGetValue(id, out var create))
                throw new ArgumentException($"BotBehaviour '{behaviour.Id}': no loop step '{id}' ({string.Join(", ", _steps.Keys)})");
            steps.Add(create());
        }
        if (steps.Count == 0)
            throw new ArgumentException($"BotBehaviour '{behaviour.Id}' has no loopSteps.");
        return new BotLoop(steps, behaviour.LoopStepTimeoutMs);
    }

    /// <summary>The readers of one bot's own state.</summary>
    public IReadOnlyList<IBotOwnReader> NewOwnReaders() => _ownReaders();

    /// <summary>True the first time <paramref name="key"/> is asked in this run (thread safe).</summary>
    public bool FirstTime(string key) => _logged.TryAdd(key, 0);
}
