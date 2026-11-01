using BotClient.Brain;
using Game.Gameplay.Entities;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Jobs;

namespace BotClient.Loop.Steps;

/// <summary>
/// The loop's socket step (WP-M3-9; spec 5.3 R-5.3-05, 11.5): a Jeweler at a jeweler's bench sockets a cut gem into the first
/// piece of gear with a free gem slot (worn gear first, then the Safe bag equipment area), through ClientCore's
/// <see cref="ClientGems"/>; argument: the gem item id (<c>socket:m3.rubyT1</c>), else the first cut gem in the Normal bag.
/// Done on the committed answer either way: <c>socket.socketed</c>, or <c>socket.failed</c> when the roll lost the gem (a
/// failed socket consumes the gem and leaves the item untouched, M3 decision 11). <c>socket.refused.&lt;reason&gt;</c> is
/// failed; with nothing to do, <c>socket.noGem</c>, <c>socket.noTarget</c>, <c>socket.noJob</c> or <c>socket.noStation</c>
/// (done in a loop, failed in a script, so an exit entry never passes without a socket).
/// </summary>
public sealed class SocketStep : IBotStep
{
    private readonly string _argument;
    private readonly List<GemTarget> _targets = new();
    private readonly List<InventorySlotEntry> _gems = new();
    private JobRules? _jobs;
    private uint _pending;

    public SocketStep(string argument = "")
    {
        _argument = argument;
    }

    public string Id => "socket";

    /// <summary>The last committed answer, for the logs and tests.</summary>
    public GemResult? Answer { get; private set; }

    public void Begin(BotStepContext context)
    {
        _pending = 0;
        Answer = null;
    }

    public BotStepStatus Tick(BotStepContext context, ref BotCommand command)
    {
        if (context.Gems is not { Rules: { } rules } gems)
            return context.NotAvailable(Id, "no gem tables" + (_argument.Length > 0 ? "; argument '" + _argument + "'" : ""));
        if (_pending != 0)
            return gems.ResultFor(_pending) is { } answer ? OnAnswer(context, answer) : BotStepStatus.Running;

        var contents = context.Inventory.Contents;
        if (contents == null || context.Jobs.State == null)
            return BotStepStatus.Running; // the first contents and jobs come at enter world
        gems.Gems(contents, _gems);
        var gem = _gems.FirstOrDefault(g => _argument.Length == 0 || rules.Item(g.ItemNetId)?.Id == _argument);
        if (gem == null)
            return Nothing(context, "noGem", _argument.Length > 0 ? $"no {_argument} in the Normal bag" : "no cut gem in the Normal bag");
        gems.Targets(contents, _targets);
        int target = _targets.FindIndex(t => t.HasFreeSlot);
        if (target < 0)
            return Nothing(context, "noTarget", "no gear with a free gem slot");
        string gemId = rules.Item(gem.ItemNetId)!.Id;
        _jobs ??= JobRules.From(context.Data.Tables);
        int level = _jobs?.Find(gems.JobFor(gemId)) is { } job && context.Jobs.Held(job.NetId) is { } held ? held.Level : 0;
        var reason = gems.CheckSocket(_targets[target], gem, level);
        if (reason == GemReason.JobRequired || reason == GemReason.JobLevelTooLow)
            return Nothing(context, "noJob", $"socketing {gemId} needs the {gems.JobFor(gemId)} job: {reason}");
        if (reason != GemReason.None)
        {
            context.Count(Id + ".refused." + reason);
            return context.Failed($"socket {gemId}: {reason}");
        }
        if (context.InCombat || !context.View.ActionIdle || context.View.Dead)
            return BotStepStatus.Running; // gem work happens out of combat (spec 5.3)
        switch (context.ApproachNpc(npc => npc.Offers(NpcInteraction.JewelerBench), ref command, out uint stationId, out _))
        {
            case NpcApproach.None:
                return Nothing(context, "noStation", "no jeweler's bench in reach");
            case NpcApproach.Walking:
                return BotStepStatus.Running;
        }
        context.SendReliable(gems.Socket(stationId, _targets[target], gem.Slot, out _pending));
        return BotStepStatus.Running;
    }

    private BotStepStatus OnAnswer(BotStepContext context, GemResult answer)
    {
        _pending = 0;
        if (answer.Reason != GemReason.None)
        {
            context.Count(Id + ".refused." + answer.Reason);
            return context.Failed($"socket refused: {answer.Reason}");
        }
        Answer = answer;
        context.Count(answer.Success ? Id + ".socketed" : Id + ".failed");
        context.Log(answer.Success
            ? $"socket: gem {answer.GemItemNetId} set in instance {answer.InstanceId}"
            : $"socket: gem {answer.GemItemNetId} lost, instance {answer.InstanceId} untouched (M3 decision 11)");
        return BotStepStatus.Done;
    }

    private BotStepStatus Nothing(BotStepContext context, string what, string why)
    {
        context.Count(Id + "." + what);
        return context.Behaviour.LoopOnce ? context.Failed(why) : BotStepStatus.Done;
    }
}
