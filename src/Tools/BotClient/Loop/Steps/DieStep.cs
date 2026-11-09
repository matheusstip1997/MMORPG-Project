using BotClient.Brain;

namespace BotClient.Loop.Steps;

/// <summary>
/// Dying in the field (spec 4.3 R-4.3-03, M4 decision 8): <c>die[:&lt;camp&gt;]</c> walks to the camp (a field zone
/// ring where the region's <c>deathDrop</c> is on) and lets its monsters kill the bot; done once the own loot bag
/// stands at the death spot.
/// <para>
/// WP-M4-12 placeholder (made real by WP-M4-9): until then it is "not yet" (no death loot bag yet), logged once per
/// run; the loop and a script go on.
/// The WP replaces this file's body (contract: Loop/Steps/README.md); the argument is the entry's
/// (<c>"die:&lt;argument&gt;"</c>), empty without one.
/// </para>
/// </summary>
public sealed class DieStep : IBotStep
{
    private readonly string _argument;

    public DieStep(string argument = "")
    {
        _argument = argument;
    }

    public string Id => "die";

    public void Begin(BotStepContext context)
    {
    }

    public BotStepStatus Tick(BotStepContext context, ref BotCommand command) =>
        context.NotAvailable(Id, "no death loot bag yet, WP-M4-9" + (_argument.Length > 0 ? "; argument '" + _argument + "'" : ""));
}
