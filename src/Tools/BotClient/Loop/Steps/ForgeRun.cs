using BotClient.Brain;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Crafting;
using Shared.Simulation.Jobs;

namespace BotClient.Loop.Steps;

/// <summary>
/// One forge session as the refine, craft and convert steps play it (WP-M3-6; spec 5.3, 11.5): choose the first definition
/// (mold or recipe, table order) the step wants that the bot can make now (the job at its level, the mold in the Safe bag,
/// every input in the Normal bag, picked by ClientCore's <see cref="ClientForge.TryPickInputs"/>), walk to a station that
/// offers it, start, use each allowed forge action once (up to <c>ForgeSettings.maxActions</c>), finish, and wait for the
/// result, which comes only after the commit. Every request waits for the answer with its id.
/// <para>
/// Counters (with the step's id): <c>.made</c>, <c>.convertFailed</c> (a failed conversion is a result, R-5.2-04),
/// <c>.refused.&lt;reason&gt;</c> (failed); with nothing to make, <c>.noMaterials</c> (done in a loop, failed in a script, so
/// an exit entry never passes without an item) and <c>.noStation</c>; "not yet" while no wanted definition of a held job makes
/// anything (content of a later WP).
/// </para>
/// </summary>
internal sealed class ForgeRun
{
    private enum Phase : byte { Idle, Starting, Acting, Finishing, Cancelling }

    private readonly string _stepId;
    private readonly Func<ForgeDefinition, bool> _wanted;
    private readonly List<ForgeInput> _inputs = new();
    private JobRules? _jobs;
    private ForgeDefinition? _definition;
    private Phase _phase;
    private uint _pending;
    private int _actionsSent;

    public ForgeRun(string stepId, Func<ForgeDefinition, bool> wanted)
    {
        _stepId = stepId;
        _wanted = wanted;
    }

    /// <summary>The last item made (its <c>ForgeResult</c>), for the logs and tests.</summary>
    public ForgeResult? Made { get; private set; }

    public void Begin()
    {
        _phase = Phase.Idle;
        _pending = 0;
        _actionsSent = 0;
        _definition = null;
    }

    public BotStepStatus Tick(BotStepContext context, ref BotCommand command)
    {
        var forge = context.Forge;
        if (forge.Rules is not { } rules)
            return context.NotAvailable(_stepId, "no forge tables");
        switch (_phase)
        {
            case Phase.Starting:
            case Phase.Acting:
                return forge.StateFor(_pending) is { } state ? OnState(context, rules, state) : BotStepStatus.Running;
            case Phase.Finishing:
                return forge.ResultFor(_pending) is { } result ? OnResult(context, result) : BotStepStatus.Running;
            case Phase.Cancelling:
                if (forge.StateFor(_pending) == null)
                    return BotStepStatus.Running;
                _phase = Phase.Idle; // a session left running was cancelled: start again
                break;
        }

        var contents = context.Inventory.Contents;
        if (contents == null || context.Jobs.State == null)
            return BotStepStatus.Running; // the first contents and jobs come at enter world
        byte normal = (byte)(context.Data.Rows<ContainerDef>().FirstOrDefault(c => c.Id == ContainerDef.NormalBag)?.Code ?? 0);
        _definition = Choose(context, rules, contents, normal);
        if (_definition == null && !HasContent(context, rules))
            return context.NotAvailable(_stepId, $"no {_stepId} recipe or mold of the held jobs makes anything yet ({ContentWp})");
        if (_definition == null)
        {
            context.Count(_stepId + ".noMaterials");
            return context.Behaviour.LoopOnce ? context.Failed($"nothing to {_stepId}: no job level, mold or materials for it") : BotStepStatus.Done;
        }
        if (context.InCombat || !context.View.ActionIdle || context.View.Dead)
            return BotStepStatus.Running; // crafting happens out of combat (spec 5.3 M3 decision 7)
        var station = _definition.Station;
        switch (context.ApproachNpc(npc => npc.Offers(station), ref command, out uint stationId, out _))
        {
            case NpcApproach.None:
                context.Count(_stepId + ".noStation");
                return context.Behaviour.LoopOnce ? context.Failed($"no {station} station in reach") : BotStepStatus.Done;
            case NpcApproach.Walking:
                return BotStepStatus.Running;
        }
        context.SendReliable(forge.Start(stationId, _definition.Kind, _definition.NetId, _inputs, out _pending));
        _phase = Phase.Starting;
        _actionsSent = 0;
        return BotStepStatus.Running;
    }

    private BotStepStatus OnState(BotStepContext context, ForgeRules rules, ForgeState state)
    {
        var forge = context.Forge;
        if (_phase == Phase.Starting && state.Reason != ForgeReason.None)
        {
            if (state.Reason == ForgeReason.SessionActive)
            {
                context.SendReliable(forge.Cancel(out _pending)); // a session of an earlier step: end it and start again
                _phase = Phase.Cancelling;
                return BotStepStatus.Running;
            }
            _phase = Phase.Idle;
            context.Count(_stepId + ".refused." + state.Reason);
            return context.Failed($"forge start refused: {state.Reason}");
        }
        var definition = _definition!;
        int actions = Math.Min(definition.Actions.Count, rules.Settings.MaxActions);
        if (state.Reason == ForgeReason.None && _actionsSent < actions)
        {
            context.SendReliable(forge.Action(definition.Actions[_actionsSent++].NetId, out _pending));
            _phase = Phase.Acting;
            return BotStepStatus.Running;
        }
        context.SendReliable(forge.Finish(out _pending));
        _phase = Phase.Finishing;
        return BotStepStatus.Running;
    }

    private BotStepStatus OnResult(BotStepContext context, ForgeResult result)
    {
        _phase = Phase.Idle;
        if (result.Reason != ForgeReason.None)
        {
            context.Count(_stepId + ".refused." + result.Reason);
            context.SendReliable(context.Forge.Cancel(out _)); // nothing was consumed: release the inputs
            return context.Failed($"forge finish refused: {result.Reason}");
        }
        Made = result;
        if (!result.Success)
        {
            context.Count(_stepId + ".convertFailed");
            context.Log($"{_stepId} {_definition?.Id}: the conversion failed, the inputs are gone (R-5.2-04)");
            return BotStepStatus.Done;
        }
        context.Count(_stepId + ".made");
        context.Log($"{_stepId} {_definition?.Id}: made {result.Count} of item {result.OutputItemNetId}, quality {result.Quality}, "
                    + $"rarity {result.Rarity}");
        return BotStepStatus.Done;
    }

    /// <summary>The first wanted definition the bot can make now, its inputs picked into <see cref="_inputs"/>.</summary>
    private ForgeDefinition? Choose(BotStepContext context, ForgeRules rules, InventoryContents contents, byte normal)
    {
        _jobs ??= JobRules.From(context.Data.Tables);
        foreach (var definition in rules.Definitions)
        {
            if (!_wanted(definition) || definition.Output == null || _jobs?.Find(definition.JobId) is not { } job
                || context.Jobs.Held(job.NetId) is not { } held || held.Level < definition.MinJobLevel)
                continue;
            if (definition.MoldItemId.Length > 0 && !HoldsMold(contents, rules, definition.MoldItemId))
                continue;
            if (context.Forge.TryPickInputs(definition, contents.Slots, normal, _inputs))
                return definition;
        }
        return null;
    }

    /// <summary>The WP that lands the forge content still missing in M3: the Jeweler's recipes and mold outputs.</summary>
    internal const string ContentWp = "WP-M3-9";

    /// <summary>
    /// Some wanted definition of a held job makes an item (whatever the level, mold or materials). Without one the step's
    /// content has not landed (the Jeweler's refine until <see cref="ContentWp"/>): "not yet", not failed (wave 1 QA F3).
    /// A bot without any job is not that case: it has no job to read the content of.
    /// </summary>
    private bool HasContent(BotStepContext context, ForgeRules rules)
    {
        if (!context.Data.Rows<JobDef>().Any(job => context.Jobs.Held(job.NetId) != null))
            return true;
        _jobs ??= JobRules.From(context.Data.Tables);
        foreach (var definition in rules.Definitions)
        {
            if (_wanted(definition) && definition.Output != null && _jobs?.Find(definition.JobId) is { } job
                && context.Jobs.Held(job.NetId) != null)
                return true;
        }
        return false;
    }

    private static bool HoldsMold(InventoryContents contents, ForgeRules rules, string moldItemId)
    {
        if (rules.Item(moldItemId) is not { } mold)
            return false;
        foreach (var container in contents.Containers)
        {
            foreach (var slot in container.Slots)
            {
                if (slot.ItemNetId == mold.NetId && !slot.Reserved)
                    return true;
            }
        }
        return false;
    }
}
