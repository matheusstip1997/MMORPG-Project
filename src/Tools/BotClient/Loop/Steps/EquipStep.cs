using BotClient.Brain;

namespace BotClient.Loop.Steps;

/// <summary>
/// The loop's equip step (WP-M2-1). Move a better piece of gear from the bag to the equipment slots
/// (equip request on the containers; spec 4.1, R-3.5-07).
/// Placeholder until that lands: "not available", and the loop goes on. TODO(WP-M2-1): replace this body, nothing else.
/// </summary>
public sealed class EquipStep : IBotStep
{
    public string Id => "equip";

    public void Begin(BotStepContext context)
    {
    }

    public BotStepStatus Tick(BotStepContext context, ref BotCommand command) =>
        context.NotAvailable(Id, "WP-M2-1: containers and equipment");
}
