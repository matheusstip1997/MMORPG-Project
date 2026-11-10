using BotClient.Brain;

namespace BotClient.Loop.Steps;

/// <summary>
/// The own loot bag (spec 4.3 R-4.3-03, M4 decision 8): <c>recoverBag</c> walks back from the respawn to the bag and
/// takes everything inside the owner window; <c>recoverBag:expire</c> leaves it alone and is done once the bag expired
/// (its items destroyed: a sink).
/// <para>
/// WP-M4-12 placeholder (made real by WP-M4-9): until then it is "not yet" (no death loot bag yet), logged once per
/// run; the loop and a script go on.
/// The WP replaces this file's body (contract: Loop/Steps/README.md); the argument is the entry's
/// (<c>"recoverBag:&lt;argument&gt;"</c>), empty without one.
/// </para>
/// </summary>
public sealed class RecoverBagStep : IBotStep
{
    private readonly string _argument;

    public RecoverBagStep(string argument = "")
    {
        _argument = argument;
    }

    public string Id => "recoverBag";

    public void Begin(BotStepContext context)
    {
    }

    public BotStepStatus Tick(BotStepContext context, ref BotCommand command) =>
        context.NotAvailable(Id, "no death loot bag yet, WP-M4-9" + (_argument.Length > 0 ? "; argument '" + _argument + "'" : ""));
}
