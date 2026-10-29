using BotClient.Brain;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Skills;

namespace BotClient.Loop.Steps;

/// <summary>
/// The loop's learn step (WP-M2-5; spec 3.5, R-3.5-04, spec 11.5 "The M2 loop"). It spends the unspent skill points
/// (<c>ProgressionState.skillPoints</c>) on the class line's trees, one <c>SkillTreeRequest</c> at a time, read from the kept
/// <c>SkillTreeState</c> with the shared <see cref="SkillTreeRules"/> (the server checks again).
/// <para>
/// The rule (the simplest sound one): first a plain node it can learn now (no evolutions: they lose points and are blocked
/// in combat), the class's kit skills before the others (their default slots are the keys the brain presses), then table
/// order; when none is left, one more level of the learned hotbar skill with the lowest level. A learned skill lands on
/// its default slot by itself (the server places it). Done when no point is left or nothing can take one.
/// </para>
/// Counters: <c>learn.skills</c>, <c>learn.upgrades</c>, <c>learn.refused.&lt;reason&gt;</c>; done at once with
/// <c>learn.nothing</c> (no points or nothing to learn).
/// </summary>
public sealed class LearnStep : IBotStep
{
    private uint _pending;
    private SkillTreeOp _pendingOp;

    public string Id => "learn";

    public void Begin(BotStepContext context) => _pending = 0;

    public BotStepStatus Tick(BotStepContext context, ref BotCommand command)
    {
        if (context.SkillTree is not { Rules: { } rules, Skills: { } skills } tree)
            return context.NotAvailable(Id, "no skill tree state yet");
        if (_pending != 0)
            return Answer(context, tree);
        if (!Choose(context, rules, skills, out var op, out string node))
        {
            context.Count("learn.nothing");
            return BotStepStatus.Done;
        }
        context.SendReliable(tree.Request(op, node, 0, out _pending));
        _pendingOp = op;
        return BotStepStatus.Running;
    }

    /// <summary>The next point's use by the rule above; false when there are no points or nothing can take one.</summary>
    internal static bool Choose(BotStepContext context, SkillRules rules, SkillState skills, out SkillTreeOp op, out string node)
    {
        op = SkillTreeOp.None;
        node = string.Empty;
        if (context.Progression.State is not { } progress || progress.SkillPoints <= 0)
            return false;
        var kit = rules.KitNodes(skills.ClassId);
        var learnable = SkillTreeRules.Available(rules, skills, progress.Level, progress.SkillPoints)
            .Where(n => n.EvolvesFrom.Length == 0)
            .OrderBy(n => kit.Contains(n.Id) ? 0 : 1)
            .FirstOrDefault();
        if (learnable != null)
        {
            (op, node) = (SkillTreeOp.Learn, learnable.Id);
            return true;
        }
        string? upgrade = skills.Learned
            .Where(pair => rules.Node(pair.Key) is { } n && n.IsHotbarSkill()
                           && SkillTreeRules.CheckUpgrade(rules, skills, pair.Key, progress.SkillPoints, out _) == SkillTreeReason.None)
            .OrderBy(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Key).FirstOrDefault();
        if (upgrade == null)
            return false;
        (op, node) = (SkillTreeOp.Upgrade, upgrade);
        return true;
    }

    private BotStepStatus Answer(BotStepContext context, Game.Gameplay.Entities.ClientSkillTree tree)
    {
        if (tree.LastResult is not { } result || result.RequestId != _pending)
            return BotStepStatus.Running;
        _pending = 0;
        if (result.Reason != SkillTreeReason.None)
        {
            context.Count("learn.refused." + result.Reason);
            return BotStepStatus.Failed;
        }
        context.Count(_pendingOp == SkillTreeOp.Learn ? "learn.skills" : "learn.upgrades");
        // The answer comes right before the state and the points that show it; the next tick chooses again from them.
        return BotStepStatus.Running;
    }
}
