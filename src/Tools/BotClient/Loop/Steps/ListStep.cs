using BotClient.Brain;

namespace BotClient.Loop.Steps;

/// <summary>
/// The auction house (spec 4.5, M4 decision 6): <c>list:&lt;item&gt;:&lt;price&gt;</c> walks to the auctioneer and
/// lists one stack of the item at the unit price (the item in escrow, the listing fee paid through <c>FeeService</c>);
/// <c>list:cancel:&lt;item&gt;</c> cancels the own listing of the item (it comes back by mail). The partner buys with
/// <c>buy:listing:&lt;item&gt;</c>.
/// <para>
/// WP-M4-12 placeholder (made real by WP-M4-7): until then it is "not yet" (no auction protocol yet), logged once per
/// run; the loop and a script go on.
/// The WP replaces this file's body (contract: Loop/Steps/README.md); the argument is the entry's
/// (<c>"list:&lt;argument&gt;"</c>), empty without one.
/// </para>
/// </summary>
public sealed class ListStep : IBotStep
{
    private readonly string _argument;

    public ListStep(string argument = "")
    {
        _argument = argument;
    }

    public string Id => "list";

    public void Begin(BotStepContext context)
    {
    }

    public BotStepStatus Tick(BotStepContext context, ref BotCommand command) =>
        context.NotAvailable(Id, "no auction protocol yet, WP-M4-7" + (_argument.Length > 0 ? "; argument '" + _argument + "'" : ""));
}
