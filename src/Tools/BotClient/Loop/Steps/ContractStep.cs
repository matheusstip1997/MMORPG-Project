using BotClient.Brain;

namespace BotClient.Loop.Steps;

/// <summary>
/// Contracts (spec 5.4, M4 decisions 10 and 11): <c>contract:propose:&lt;mold or recipe&gt;</c> (the customer proposes
/// it with its materials and gold in escrow), <c>contract:craft</c> (the crafter accepts the partner's proposal at the
/// board, forges it at a station, and the output goes to the customer). Both sides meet at the point <c>contract</c>.
/// <para>
/// WP-M4-12 placeholder (made real by WP-M4-10): until then it is "not yet" (no contract protocol yet), logged once per
/// run; the loop and a script go on.
/// The WP replaces this file's body (contract: Loop/Steps/README.md); the argument is the entry's
/// (<c>"contract:&lt;argument&gt;"</c>), empty without one.
/// </para>
/// </summary>
public sealed class ContractStep : IBotStep
{
    private readonly string _argument;

    public ContractStep(string argument = "")
    {
        _argument = argument;
    }

    public string Id => "contract";

    public void Begin(BotStepContext context)
    {
    }

    public BotStepStatus Tick(BotStepContext context, ref BotCommand command) =>
        context.NotAvailable(Id, "no contract protocol yet, WP-M4-10" + (_argument.Length > 0 ? "; argument '" + _argument + "'" : ""));
}
