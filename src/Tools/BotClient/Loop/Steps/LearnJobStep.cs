using BotClient.Brain;
using Shared.Protocol;
using Shared.Simulation.Jobs;

namespace BotClient.Loop.Steps;

/// <summary>
/// The loop's learnJob step (WP-M3-7; spec 5.1 R-5.1-04, spec 11.5). It spends a held job's unspent job points on its skill
/// tree, one <c>JobTreeRequest</c> at a time, read from ClientCore's <c>ClientJobs</c> (<c>context.Jobs</c>: the committed
/// nodes and points of <c>JobState</c>) with the shared <see cref="JobTreeRules"/>, so it never asks for a learn the server
/// refuses by rule (the server checks again). The job is the argument (a <c>JobDef</c> id, <c>learnJob:blacksmith</c>), or
/// the main job without one.
/// <para>
/// The rule (the simplest sound one): the first node in table order that can take a level now. Learning works anywhere
/// and in combat (decision 10), so the step never walks. Done when no point is left or nothing can take one.
/// </para>
/// Counters: <c>learnJob.levels</c>, <c>learnJob.refused.&lt;reason&gt;</c> (failed); done at once with <c>learnJob.nothing</c>
/// (no points, nothing to learn, or the job is not held) or <c>learnJob.waiting</c> (no job state yet); "not available" for
/// an argument naming no <c>JobDef</c> row or without the job tree tables.
/// </summary>
public sealed class LearnJobStep : IBotStep
{
    private readonly string _argument;
    private JobRules? _rules;
    private uint _pending;

    public LearnJobStep(string argument = "")
    {
        _argument = argument;
    }

    public string Id => "learnJob";

    public void Begin(BotStepContext context) => _pending = 0;

    public BotStepStatus Tick(BotStepContext context, ref BotCommand command)
    {
        if (_pending != 0)
            return Answer(context);
        _rules ??= JobRules.From(context.Data.Tables);
        if (_rules?.Tree is not { } tree)
            return context.NotAvailable(Id, "no job tree tables");
        if (_argument.Length > 0 && _rules.Find(_argument) == null)
            return context.NotAvailable(Id, $"no JobDef '{_argument}'");
        if (context.Jobs.State == null)
        {
            context.Count("learnJob.waiting");
            return BotStepStatus.Done;
        }
        if (!Choose(context.Jobs, _rules, tree, _argument, out byte jobNetId, out string node))
        {
            context.Count("learnJob.nothing");
            return BotStepStatus.Done;
        }
        context.SendReliable(context.Jobs.TreeRequest(JobTreeOp.Learn, jobNetId, node, out _pending));
        return BotStepStatus.Running;
    }

    /// <summary>The next point's node by the rule above; false when the job is not held, has no points or nothing can take one.</summary>
    internal static bool Choose(Game.Gameplay.Entities.ClientJobs jobs, JobRules rules, JobTreeRules tree, string jobId, out byte jobNetId,
        out string node)
    {
        jobNetId = 0;
        node = string.Empty;
        var held = jobId.Length > 0 ? (rules.Find(jobId) is { } def ? jobs.Held(def.NetId) : null) : jobs.InSlot(JobSlot.Main);
        if (held == null || held.Points == 0 || rules.FindByNetId(held.JobNetId) is not { } job)
            return false;
        var learned = jobs.Learned(held.JobNetId);
        foreach (var candidate in tree.Nodes(job.Id))
        {
            if (tree.CheckLearn(job.Id, true, held.Level, learned, candidate.Id, held.Points, out _) == JobTreeCheck.Ok)
            {
                (jobNetId, node) = (held.JobNetId, candidate.Id);
                return true;
            }
        }
        return false;
    }

    private BotStepStatus Answer(BotStepContext context)
    {
        if (context.Jobs.LastTreeResult is not { } result || result.RequestId != _pending)
            return BotStepStatus.Running;
        _pending = 0;
        if (result.Reason != JobReason.None)
        {
            context.Count("learnJob.refused." + result.Reason);
            return BotStepStatus.Failed;
        }
        context.Count("learnJob.levels");
        // The answer comes right before the state that shows it; the next tick chooses again from it.
        return BotStepStatus.Running;
    }
}
