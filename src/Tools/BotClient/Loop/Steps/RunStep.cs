using BotClient.Brain;

namespace BotClient.Loop.Steps;

/// <summary>
/// The loop's run step (WP-M2-6). At the outpost board, build a run, play its steps and claim the exp (spec 5.5).
/// Placeholder until that lands: "not available", and the loop goes on. TODO(WP-M2-6): replace this body, nothing else.
/// </summary>
public sealed class RunStep : IBotStep
{
    public string Id => "run";

    public void Begin(BotStepContext context)
    {
    }

    public BotStepStatus Tick(BotStepContext context, ref BotCommand command) =>
        context.NotAvailable(Id, "WP-M2-6: outpost quests and runs");
}
