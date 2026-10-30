using BotClient.Brain;

namespace BotClient.Loop.Steps;

/// <summary>
/// The level check of the M3 exit run (WP-M3-12; plan section 1, step 1: the character has the job level of the unlock
/// schedule). It waits for the own level (<c>ProgressionState</c>, kept by ClientCore's <c>ClientProgression</c>) and is
/// done when it reaches the argument (<c>level:12</c>), failed below it (<c>level.below</c>: the test seed
/// <c>BotScenario.seedLevel</c> did not apply); without an argument it is done once a level is known.
/// Counters: <c>level.reached</c>, <c>level.below</c>.
/// </summary>
public sealed class LevelStep : IBotStep
{
    private readonly int _target;

    public LevelStep(string argument = "")
    {
        _target = argument.Length == 0 ? 0
            : int.TryParse(argument, out int level) && level > 0 ? level
            : throw new ArgumentException($"level step: '{argument}' is not a level");
    }

    public string Id => "level";

    public void Begin(BotStepContext context)
    {
    }

    public BotStepStatus Tick(BotStepContext context, ref BotCommand command)
    {
        int? level = context.Progression.State?.Level ?? context.Own.Level;
        if (level == null)
            return BotStepStatus.Running; // ProgressionState comes with enter world
        if (level < _target)
        {
            context.Count("level.below");
            return context.Failed($"level {level} is below {_target} (the seed did not apply?)");
        }
        context.Count("level.reached");
        return BotStepStatus.Done;
    }
}
