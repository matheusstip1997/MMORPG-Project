using BotClient.Brain;

namespace BotClient.Loop.Steps;

/// <summary>
/// The loop's buy step (WP-M2-4). At the NPC vendor, buy what the behaviour asks for (a potion) with the gold from the sales
/// (<c>NpcVendorStock</c>; spec 4.2).
/// Placeholder until that lands: "not available", and the loop goes on. TODO(WP-M2-4): replace this body, nothing else.
/// </summary>
public sealed class BuyStep : IBotStep
{
    public string Id => "buy";

    public void Begin(BotStepContext context)
    {
    }

    public BotStepStatus Tick(BotStepContext context, ref BotCommand command) =>
        context.NotAvailable(Id, "WP-M2-4: NPC vendor");
}
