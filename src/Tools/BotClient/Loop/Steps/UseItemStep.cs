using BotClient.Brain;

namespace BotClient.Loop.Steps;

/// <summary>
/// The loop's useItem step (WP-M3-12 placeholder; made real by WP-M3-3). It uses a consumable from the bag (the HP potion below an HP threshold, an action allowed in combat); in the M3 exit run it drinks the bought potion in combat (HP and TOX rise); argument: the item id.
/// <para>
/// Until then it is "not yet" (no use-item protocol yet, WP-M3-3), logged once per run; the loop and a script go on. The WP replaces this file's body
/// (contract: Loop/Steps/README.md); the argument is the entry's (<c>"useItem:&lt;argument&gt;"</c>), empty without one.
/// </para>
/// </summary>
public sealed class UseItemStep : IBotStep
{
    private readonly string _argument;

    public UseItemStep(string argument = "")
    {
        _argument = argument;
    }

    public string Id => "useItem";

    public void Begin(BotStepContext context)
    {
    }

    public BotStepStatus Tick(BotStepContext context, ref BotCommand command) =>
        context.NotAvailable(Id, "no use-item protocol yet, WP-M3-3" + (_argument.Length > 0 ? "; argument '" + _argument + "'" : ""));
}
