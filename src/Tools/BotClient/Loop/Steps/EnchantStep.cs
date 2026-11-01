using BotClient.Brain;
using Game.Gameplay.Entities;
using Shared.Data.Tables;
using Shared.Protocol;

namespace BotClient.Loop.Steps;

/// <summary>
/// The loop's enchant step (WP-M3-8; spec 5.3 R-5.3-04, 11.5): one enchant attempt, the way a player makes it. It picks an
/// item of one of its jobs below +9 (ClientCore's <see cref="ClientEnchant.Candidates"/>: Equipped first, then the Safe bag's
/// equipment area; with an argument, the first of that <c>ItemDef</c> id, <c>"enchant:m3.forgedSword"</c>), checks that the
/// Normal bag holds the stones of its tier, walks to a station of the item's job, sends one <c>EnchantRequest</c> out of
/// combat and waits for the answer with its id, which comes only after the commit. The stones are gone whatever the outcome.
/// <para>
/// Counters: <c>enchant.attempt</c>, <c>enchant.up</c>, <c>enchant.same</c>, <c>enchant.down</c>,
/// <c>enchant.refused.&lt;reason&gt;</c> (failed); with nothing to enchant <c>enchant.noItem</c>, without the stones
/// <c>enchant.noStones</c>, without a station <c>enchant.noStation</c> (each done in a loop, failed in a script, so an exit
/// entry never passes without an attempt).
/// </para>
/// </summary>
public sealed class EnchantStep : IBotStep
{
    private static readonly NpcInteraction[] Stations = { NpcInteraction.Forge, NpcInteraction.JewelerBench };

    private readonly string _argument;
    private readonly List<EnchantCandidate> _candidates = new();
    private uint _pending;

    public EnchantStep(string argument = "")
    {
        _argument = argument;
    }

    public string Id => "enchant";

    /// <summary>The last answer of an attempt that was made, for the logs and tests.</summary>
    public EnchantResult? Made { get; private set; }

    public void Begin(BotStepContext context)
    {
        _pending = 0;
    }

    public BotStepStatus Tick(BotStepContext context, ref BotCommand command)
    {
        var enchant = context.Enchant;
        if (enchant.Rules is not { } rules)
            return context.NotAvailable(Id, "no enchant tables");
        if (_pending != 0)
            return enchant.ResultFor(_pending) is { } result ? OnResult(context, result) : BotStepStatus.Running;

        var contents = context.Inventory.Contents;
        var jobs = context.Jobs.State;
        if (contents == null || jobs == null)
            return BotStepStatus.Running; // the first contents and jobs come at enter world
        if (Choose(context, contents, jobs, out var station) is not { } chosen)
        {
            context.Count(Id + ".noItem");
            return End(context, "nothing to enchant: no item of a held job below +9"
                                + (_argument.Length > 0 ? $" ('{_argument}')" : ""));
        }
        if (enchant.StonesHeld(contents, chosen.Stone) < chosen.StoneCount)
        {
            context.Count(Id + ".noStones");
            return End(context, $"needs {chosen.StoneCount} {chosen.Stone.Id} to enchant {chosen.Item.Id}");
        }
        if (context.InCombat || !context.View.ActionIdle || context.View.Dead)
            return BotStepStatus.Running; // enchanting happens out of combat (spec 5.3 M3 decision 7)
        switch (context.ApproachNpc(npc => npc.Offers(station), ref command, out uint stationId, out _))
        {
            case NpcApproach.None:
                context.Count(Id + ".noStation");
                return End(context, $"no {station} station in reach");
            case NpcApproach.Walking:
                return BotStepStatus.Running;
        }
        context.SendReliable(enchant.Request(stationId, chosen.Container, chosen.Slot, chosen.InstanceId, out _pending));
        context.Count(Id + ".attempt");
        return BotStepStatus.Running;
    }

    private BotStepStatus OnResult(BotStepContext context, EnchantResult result)
    {
        _pending = 0;
        if (result.Reason != EnchantReason.None)
        {
            context.Count(Id + ".refused." + result.Reason);
            return context.Failed($"enchant refused: {result.Reason}");
        }
        Made = result;
        string outcome = result.ToLevel > result.FromLevel ? "up" : result.ToLevel < result.FromLevel ? "down" : "same";
        context.Count(Id + "." + outcome);
        context.Log($"enchant item {result.ItemNetId}: +{result.FromLevel} -> +{result.ToLevel}, {result.StonesUsed} stone(s) consumed");
        return BotStepStatus.Done;
    }

    /// <summary>The first item to enchant (the argument's id, else any) and the station it enchants at.</summary>
    private EnchantCandidate? Choose(BotStepContext context, InventoryContents contents, JobState jobs, out NpcInteraction station)
    {
        var rules = context.Enchant.Rules!;
        foreach (var kind in Stations)
        {
            context.Enchant.Candidates(contents, kind, job => rules.Holds(jobs, job), _candidates);
            foreach (var candidate in _candidates)
            {
                if (candidate.Maxed || (_argument.Length > 0 && candidate.Item.Id != _argument))
                    continue;
                station = kind;
                return candidate;
            }
        }
        station = NpcInteraction.None;
        return null;
    }

    private BotStepStatus End(BotStepContext context, string why) =>
        context.Behaviour.LoopOnce ? context.Failed(why) : BotStepStatus.Done;
}
