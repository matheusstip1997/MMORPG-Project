using BotClient.Brain;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Stats;

namespace BotClient.Loop.Steps;

/// <summary>
/// The loop's allocate step (WP-M2-13; spec 3.4 R-3.4-08, spec 11.5 "The M2 loop"). It places the unspent free attribute
/// points (<c>ProgressionState.attributePoints</c>) one <c>AttributeSpendRequest</c> at a time, out of combat (the server
/// refuses a placement in combat or during an action, user decision 2026-10-29).
/// <para>
/// The rule: the class's entry of <c>BotBehaviour.loopAttributeOrder</c> (<c>"warrior:str,sta"</c>; a tier-1 class uses its
/// root's entry) lists the attributes the class builds; each point goes to the listed one with the fewest placed points (ties
/// in list order). A class with no entry puts every point on its fastest-growing attribute (<c>ClassDef</c> growth; ties in
/// STR, DEX, STA, INT order). Done when no point is left.
/// </para>
/// Counters: <c>allocate.points</c>, <c>allocate.refused.&lt;reason&gt;</c>; done at once with <c>allocate.nothing</c> (no
/// point), <c>allocate.inCombat</c> (the next round tries again) or <c>allocate.waiting</c> (no progression state yet).
/// </summary>
public sealed class AllocateStep : IBotStep
{
    private uint _pending;

    public string Id => "allocate";

    public void Begin(BotStepContext context) => _pending = 0;

    public BotStepStatus Tick(BotStepContext context, ref BotCommand command)
    {
        if (_pending != 0)
            return Answer(context);
        if (context.Progression.State is not { } progress)
        {
            context.Count("allocate.waiting");
            return BotStepStatus.Done;
        }
        if (progress.AttributePoints <= 0)
        {
            context.Count("allocate.nothing");
            return BotStepStatus.Done;
        }
        if (context.InCombat)
        {
            context.Count("allocate.inCombat");
            return BotStepStatus.Done;
        }
        int attribute = Choose(context.Data.Tables, context.Behaviour, ClassOf(context), progress);
        context.SendReliable(context.Progression.Request((byte)attribute, out _pending));
        return BotStepStatus.Running;
    }

    /// <summary>The committed class (the skill tree's after an advance), else the class the bot was made with.</summary>
    private static string ClassOf(BotStepContext context) => context.SkillTree?.Skills?.ClassId is { Length: > 0 } classId ? classId : context.ClassId;

    /// <summary>The attribute (<c>Attributes.Str</c> ... <c>Int</c>) the next point goes to, by the rule above.</summary>
    internal static int Choose(Shared.Data.ConfigSet tables, BotBehaviour behaviour, string classId, ProgressionState progress)
    {
        var classes = tables.TryGet(typeof(ClassDef), out var table) ? table.Rows.Cast<ClassDef>().ToList() : new List<ClassDef>();
        var order = Order(behaviour, classId, classes);
        if (order.Count > 0)
            return order.OrderBy(a => progress.Placed(a)).ThenBy(order.IndexOf).First();
        var classDef = classes.FirstOrDefault(c => c.Id == classId);
        if (classDef == null)
            return Attributes.Str;
        int[] growth = { classDef.StrGrowth, classDef.DexGrowth, classDef.StaGrowth, classDef.IntGrowth };
        int best = 0;
        for (int a = 1; a < Attributes.Count; a++)
        {
            if (growth[a] > growth[best])
                best = a;
        }
        return best;
    }

    /// <summary>The class's listed attributes, walking up to its root (<c>ClassDef.parent</c>) when it has no entry of its own.</summary>
    private static List<int> Order(BotBehaviour behaviour, string classId, List<ClassDef> classes)
    {
        for (string? id = classId; !string.IsNullOrEmpty(id); id = classes.FirstOrDefault(c => c.Id == id)?.Parent)
        {
            foreach (var entry in behaviour.LoopAttributeOrder)
            {
                if (BotBehaviour.TryParseAttributeOrder(entry, out string entryClass, out var attributes) && entryClass == id)
                    return attributes.ToList();
            }
        }
        return new List<int>();
    }

    private BotStepStatus Answer(BotStepContext context)
    {
        if (context.Progression.LastAttributeResult is not { } result || result.RequestId != _pending)
            return BotStepStatus.Running;
        _pending = 0;
        if (result.Reason != AttributeReason.None)
        {
            context.Count("allocate.refused." + result.Reason);
            return BotStepStatus.Failed;
        }
        context.Count("allocate.points");
        // The answer comes right before the progression state that shows it; the next tick chooses again from it.
        return BotStepStatus.Running;
    }
}
