using BotClient.Brain;

namespace BotClient.Loop.Steps;

/// <summary>
/// The mailbox (spec 4.3, 4.4, 4.5, M4 decision 4): <c>mail:claim</c> waits for a system mail (listing proceeds, a
/// returned item) and claims its attachments into the bag (a class 1 operation answered after the commit). Not a pair
/// step: the mail comes from the server.
/// <para>
/// WP-M4-12 placeholder (made real by WP-M4-3): until then it is "not yet" (no mail protocol yet), logged once per run;
/// the loop and a script go on.
/// The WP replaces this file's body (contract: Loop/Steps/README.md); the argument is the entry's
/// (<c>"mail:&lt;argument&gt;"</c>), empty without one.
/// </para>
/// </summary>
public sealed class MailStep : IBotStep
{
    private readonly string _argument;

    public MailStep(string argument = "")
    {
        _argument = argument;
    }

    public string Id => "mail";

    public void Begin(BotStepContext context)
    {
    }

    public BotStepStatus Tick(BotStepContext context, ref BotCommand command) =>
        context.NotAvailable(Id, "no mail protocol yet, WP-M4-3" + (_argument.Length > 0 ? "; argument '" + _argument + "'" : ""));
}
