using BotClient.Brain;

namespace BotClient.Loop.Steps;

/// <summary>
/// The loop's learn step (WP-M2-5). Spend the skill points of the levels gained on the class's skill tree (spec 3.5, R-3.5-04).
/// Placeholder until that lands: "not available", and the loop goes on. TODO(WP-M2-5): replace this body, nothing else.
/// </summary>
public sealed class LearnStep : IBotStep
{
    public string Id => "learn";

    public void Begin(BotStepContext context)
    {
    }

    public BotStepStatus Tick(BotStepContext context, ref BotCommand command) =>
        context.NotAvailable(Id, "WP-M2-5: skill tree");
}
