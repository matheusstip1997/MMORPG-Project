using BotClient.Brain;
using Game.Gameplay.Entities;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;
using Shared.World;

namespace BotClient.Loop.Steps;

/// <summary>
/// The loop's gather step (WP-M3-5; spec 5.2, 11.5): the bot walks to the nearest available resource node of one of its jobs
/// whose tool it holds in the Safe bag's equipment area (<c>ClientHarvest.BestToolTier</c>), the way a player does: a node
/// entity in interest, else the nearest <c>GatherNodes</c> marker of such a node type; it stops within the node's
/// <c>reachCm</c> of its predicted and its confirmed position (the server's rule), sends one <c>HarvestRequest</c> once its
/// own action is idle, plays the node's harvest action in the predictor (standing still: a movement input breaks the
/// channel) and waits for the answer, which comes after the commit.
/// <para>
/// The argument (<c>"gather:&lt;argument&gt;"</c>) is the number of harvests (default 1), or a <c>NodeDef</c> id to harvest
/// only that node type once. A node depleted under it or a broken channel is tried again elsewhere, up to
/// <see cref="MaxRetries"/> times.
/// </para>
/// Counters: <c>gather.harvested</c>, <c>gather.units</c>, <c>gather.refused.&lt;reason&gt;</c>; done at once (failed in a script) with
/// <c>gather.noJob</c> (no own job has a node type), <c>gather.noTool</c> (no tool of a reachable tier) or
/// <c>gather.noNode</c> (every marker of its node types visited and none available).
/// </summary>
public sealed class GatherStep : IBotStep
{
    /// <summary>Refusals that send the bot to another node (a node depleted under it, a broken channel) before it gives up.</summary>
    public const int MaxRetries = 3;

    private readonly int _wanted = 1;
    private readonly string _nodeId = string.Empty;
    private readonly HashSet<string> _visited = new(StringComparer.Ordinal);
    private int _done;
    private int _retries;
    private uint _pending;

    /// <param name="argument">The number of harvests, or a <c>NodeDef</c> id; empty for one harvest of any own node type.</param>
    public GatherStep(string argument = "")
    {
        argument ??= string.Empty;
        if (int.TryParse(argument, out int count) && count > 0)
            _wanted = count;
        else
            _nodeId = argument;
    }

    public string Id => "gather";

    public void Begin(BotStepContext context)
    {
        _done = 0;
        _retries = 0;
        _pending = 0;
        _visited.Clear();
    }

    public BotStepStatus Tick(BotStepContext context, ref BotCommand command)
    {
        if (context.Harvest is not { } harvest || context.Nodes is not { } nodes)
            return context.NotAvailable(Id, "no ClientHarvest in this context");
        if (_pending != 0)
            return Answer(context, harvest);

        var types = NodeTypes(context, harvest);
        if (types.Count == 0)
            return Nothing(context,
                context.Data.Rows<NodeDef>().Any(n => context.Jobs.Has(n.JobId) && Wanted(n)) ? "gather.noTool" : "gather.noJob");

        var self = context.View.Self;
        // A node entity in interest: the nearest available one of those types (ties by entity id, so every run goes alike).
        uint nodeId = 0;
        NodeDef? node = null;
        FixVec2 at = default;
        double best = double.MaxValue;
        foreach (var entity in context.Entities.EntityValues)
        {
            if (entity.Kind != EntityKind.ResourceNode || (entity.Flags & EntityFlags.Depleted) != 0
                || !nodes.TryGetNode(entity.DefId, out var def) || !types.Contains(def.Id) || !entity.Samples.TryNewest(out var sample))
                continue;
            var position = FixVec2.FromCm(sample.XCm, sample.ZCm); // snapshot axes: Z is ground Y
            if (!context.Data.CanEnter(position, context.Own.Level))
                continue;
            double cm = GridPath.DistanceCm(self, position);
            if (cm < best || (cm == best && entity.Id < nodeId))
            {
                nodeId = entity.Id;
                node = def;
                at = position;
                best = cm;
            }
        }
        if (node != null)
        {
            double walkTo = node.ReachCm * context.Behaviour.ApproachReachPercent / 100.0;
            if (best > walkTo || GridPath.DistanceCm(context.View.Confirmed, at) > node.ReachCm)
            {
                if (best > walkTo)
                    command = context.Walk(at, command);
                return BotStepStatus.Running;
            }
            if (!context.View.ActionIdle)
                return BotStepStatus.Running; // the server refuses a harvest during another action (ActionBusy)
            var reason = harvest.Check(node, context.Jobs, context.Inventory.Contents, context.NowMs);
            if (reason != HarvestReason.None)
                return Refused(context, reason);
            context.SendReliable(harvest.Request(nodeId, context.NowMs, out _pending));
            context.PlayAction?.Invoke(node.ActionId);
            return BotStepStatus.Running;
        }

        // None in interest: the nearest marker of those types not visited yet (sorted by id for ties).
        string? marker = null;
        best = double.MaxValue;
        foreach (var row in context.Data.Rows<GatherNodes>().OrderBy(g => g.MarkerId, StringComparer.Ordinal))
        {
            if (!types.Contains(row.NodeId) || _visited.Contains(row.MarkerId)
                || !context.Data.Markers.TryGetValue(row.MarkerId, out var position)
                || !context.Data.CanEnter(position, context.Own.Level))
                continue;
            double cm = GridPath.DistanceCm(self, position);
            if (cm < best)
            {
                best = cm;
                marker = row.MarkerId;
            }
        }
        if (marker == null)
            return Nothing(context, "gather.noNode");
        if (best <= MarkerArrivalCm)
            _visited.Add(marker); // standing there and no available node: it is depleted, try the next one
        else
            command = context.Walk(context.Data.Markers[marker], command);
        return BotStepStatus.Running;
    }

    /// <summary>A marker this close and no available node in interest means the node there is depleted.</summary>
    private const double MarkerArrivalCm = 150;

    /// <summary>The <c>NodeDef</c> ids of the own jobs whose tool the bot holds at their tier (and the argument's, if any).</summary>
    private HashSet<string> NodeTypes(BotStepContext context, ClientHarvest harvest)
    {
        var types = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in context.Data.Rows<NodeDef>())
        {
            if (Wanted(node) && context.Jobs.Has(node.JobId)
                && harvest.BestToolTier(context.Inventory.Contents, node.ToolType) >= node.MinToolTier)
                types.Add(node.Id);
        }
        return types;
    }

    private bool Wanted(NodeDef node) => _nodeId.Length == 0 || node.Id == _nodeId;

    private BotStepStatus Answer(BotStepContext context, ClientHarvest harvest)
    {
        if (harvest.LastResult is not { } result || result.RequestId != _pending)
            return harvest.IsWaiting(context.NowMs) ? BotStepStatus.Running : Lost(context);
        _pending = 0;
        if (result.Reason != HarvestReason.None)
            return Refused(context, result.Reason);
        context.Count("gather.harvested");
        context.Count("gather.units", result.Count);
        _done++;
        return _done >= _wanted ? BotStepStatus.Done : BotStepStatus.Running;
    }

    /// <summary>A node depleted under the bot or a broken channel sends it on (up to <see cref="MaxRetries"/>); others fail the step.</summary>
    private BotStepStatus Refused(BotStepContext context, HarvestReason reason)
    {
        context.Count("gather.refused." + reason);
        bool retry = reason is HarvestReason.NodeDepleted or HarvestReason.HarvestInterrupted or HarvestReason.ActionBusy;
        return retry && ++_retries <= MaxRetries ? BotStepStatus.Running : context.Failed("harvest refused: " + reason);
    }

    /// <summary>
    /// Nothing to harvest: done in a loop, failed in a script, so an exit entry never passes without a harvest (as
    /// <see cref="ForgeRun"/>'s <c>.noMaterials</c>; the wave 1 fix run's <c>gather:6</c> was done with no pickaxe).
    /// </summary>
    private static BotStepStatus Nothing(BotStepContext context, string counter)
    {
        context.Count(counter);
        return context.Behaviour.LoopOnce ? context.Failed($"nothing to gather ({counter})") : BotStepStatus.Done;
    }

    private static BotStepStatus Lost(BotStepContext context)
    {
        context.Count("gather.noAnswer");
        return context.Failed("no harvest answer");
    }
}
