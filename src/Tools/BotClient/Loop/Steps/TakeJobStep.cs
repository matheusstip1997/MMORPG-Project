using BotClient.Brain;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Jobs;

namespace BotClient.Loop.Steps;

/// <summary>
/// The loop's takeJob step (WP-M3-1; spec 5.1 M3 decisions, spec 11.5). It walks to the city job master and picks the
/// entry's job (argument: a <c>JobDef</c> id, <c>takeJob:blacksmith</c>; without one the <c>JobDef</c> rows in id order,
/// round robin by bot index) as the main job, or as the secondary once a different main is held. The pick is permanent and
/// answered only after its save committed; the jobs are read from ClientCore's <c>ClientJobs</c> (<c>context.Jobs</c>, the
/// <c>JobState</c> sent at enter world). The step uses the server's own pick rules (<see cref="JobRules.CheckPick"/>) so it
/// never asks for a pick the server refuses by rule.
/// <para>
/// Counters: <c>takeJob.picked</c>, <c>takeJob.refused.&lt;reason&gt;</c> (failed); done at once with <c>takeJob.held</c> (the
/// job is held, or both slots are), <c>takeJob.tooLow</c> (below the job's <c>minLevel</c>: the loop plays the M2 steps until
/// then), <c>takeJob.noNpc</c> or <c>takeJob.waiting</c> (no job or progression state yet); "not available" for an argument
/// naming no <c>JobDef</c> row.
/// </para>
/// </summary>
public sealed class TakeJobStep : IBotStep
{
    private readonly string _argument;
    private JobRules? _rules;
    private uint _pending;
    private JobDef? _asked;

    public TakeJobStep(string argument = "")
    {
        _argument = argument;
    }

    public string Id => "takeJob";

    public void Begin(BotStepContext context) => _pending = 0;

    public BotStepStatus Tick(BotStepContext context, ref BotCommand command)
    {
        if (_pending != 0)
            return Answer(context);
        _rules ??= JobRules.From(context.Data.Tables);
        if (_rules == null || _rules.Jobs.Count == 0)
            return context.NotAvailable(Id, "no job tables");
        var job = _argument.Length > 0 ? _rules.Find(_argument) : RoundRobin(_rules, context.BotIndex);
        if (job == null)
            return context.NotAvailable(Id, $"no JobDef '{_argument}'");
        if (context.Jobs.State == null || context.Progression.State is not { } progress)
        {
            context.Count("takeJob.waiting");
            return BotStepStatus.Done;
        }
        string? main = IdIn(context, JobSlot.Main);
        string? secondary = IdIn(context, JobSlot.Secondary);
        if (main == job.Id || secondary == job.Id || (main != null && secondary != null))
        {
            context.Count("takeJob.held");
            return BotStepStatus.Done;
        }
        var slot = main == null ? JobSlot.Main : JobSlot.Secondary;
        switch (JobRules.CheckPick(job, slot, progress.Level, main, secondary))
        {
            case JobPickCheck.Ok:
                break;
            case JobPickCheck.LevelTooLow:
                context.Count("takeJob.tooLow");
                return BotStepStatus.Done;
            default:
                context.Count("takeJob.held"); // the job cannot fill the open slot (not a secondary job): nothing to pick
                return BotStepStatus.Done;
        }
        switch (context.ApproachNpc(npc => npc.Offers(NpcInteraction.Jobs), ref command, out uint npcEntityId, out _))
        {
            case NpcApproach.None:
                context.Count("takeJob.noNpc");
                return BotStepStatus.Done;
            case NpcApproach.Walking:
                return BotStepStatus.Running;
        }
        _asked = job;
        context.SendReliable(context.Jobs.Request(npcEntityId, (byte)job.NetId, slot, out _pending));
        return BotStepStatus.Running;
    }

    private BotStepStatus Answer(BotStepContext context)
    {
        if (context.Jobs.LastResult is not { } answer || answer.RequestId != _pending)
            return BotStepStatus.Running; // answered after the commit
        _pending = 0;
        if (answer.Reason == JobReason.None)
        {
            context.Count("takeJob.picked");
            context.Log($"took job {_asked?.Id} as {answer.Slot}");
            return BotStepStatus.Done;
        }
        context.Count("takeJob.refused." + answer.Reason);
        return BotStepStatus.Failed;
    }

    /// <summary>The job of a bot without an argument: the rows in id order, round robin by bot index.</summary>
    private static JobDef RoundRobin(JobRules rules, int botIndex)
    {
        var ordered = rules.Jobs.OrderBy(j => j.Id, StringComparer.Ordinal).ToList();
        return ordered[(int)((uint)botIndex % (uint)ordered.Count)];
    }

    private string? IdIn(BotStepContext context, JobSlot slot) =>
        context.Jobs.InSlot(slot) is { } held ? _rules?.FindByNetId(held.JobNetId)?.Id ?? held.JobNetId.ToString() : null;
}
