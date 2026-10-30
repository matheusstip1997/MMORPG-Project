using BotClient.Brain;

namespace BotClient.Loop.Steps;

/// <summary>
/// The loop's socket step (WP-M3-12 placeholder; made real by WP-M3-9). A Jeweler at the bench sockets a cut gem into a piece of gear with a free gem slot (R-5.3-05; a failed socket consumes the gem); argument: the gem item id.
/// <para>
/// Until then it is "not yet" (no socket protocol yet, WP-M3-9), logged once per run; the loop and a script go on. The WP replaces this file's body
/// (contract: Loop/Steps/README.md); the argument is the entry's (<c>"socket:&lt;argument&gt;"</c>), empty without one.
/// </para>
/// </summary>
public sealed class SocketStep : IBotStep
{
    private readonly string _argument;

    public SocketStep(string argument = "")
    {
        _argument = argument;
    }

    public string Id => "socket";

    public void Begin(BotStepContext context)
    {
    }

    public BotStepStatus Tick(BotStepContext context, ref BotCommand command) =>
        context.NotAvailable(Id, "no socket protocol yet, WP-M3-9" + (_argument.Length > 0 ? "; argument '" + _argument + "'" : ""));
}
