using BotClient.Brain;

namespace BotClient.Loop.Steps;

/// <summary>
/// The loop's sell step (WP-M2-4). Walk to the nearest outpost NPC, ask for a quote and sell the drops in the bag
/// (<c>OutpostInteractRequest</c> and the sale request and result; spec 4.2, a class 1 transaction).
/// Placeholder until that lands: "not available", and the loop goes on. TODO(WP-M2-4): replace this body, nothing else.
/// </summary>
public sealed class SellStep : IBotStep
{
    public string Id => "sell";

    public void Begin(BotStepContext context)
    {
    }

    public BotStepStatus Tick(BotStepContext context, ref BotCommand command) =>
        context.NotAvailable(Id, "WP-M2-4: outposts and selling");
}
