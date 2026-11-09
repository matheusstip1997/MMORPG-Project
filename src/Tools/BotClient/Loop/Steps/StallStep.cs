using BotClient.Brain;

namespace BotClient.Loop.Steps;

/// <summary>
/// Shop stalls (spec 4.5, M4 decision 7): <c>stall:open:&lt;item&gt;:&lt;price&gt;</c> opens a stall on a free city
/// place with the item; <c>stall:close</c> closes it (the unsold items come back by mail). The partner buys with
/// <c>buy:stall</c>.
/// <para>
/// WP-M4-12 placeholder (made real by WP-M4-8): until then it is "not yet" (no stall protocol yet), logged once per
/// run; the loop and a script go on.
/// The WP replaces this file's body (contract: Loop/Steps/README.md); the argument is the entry's
/// (<c>"stall:&lt;argument&gt;"</c>), empty without one.
/// </para>
/// </summary>
public sealed class StallStep : IBotStep
{
    private readonly string _argument;

    public StallStep(string argument = "")
    {
        _argument = argument;
    }

    public string Id => "stall";

    public void Begin(BotStepContext context)
    {
    }

    public BotStepStatus Tick(BotStepContext context, ref BotCommand command) =>
        context.NotAvailable(Id, "no stall protocol yet, WP-M4-8" + (_argument.Length > 0 ? "; argument '" + _argument + "'" : ""));
}
