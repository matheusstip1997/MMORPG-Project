using BotClient.Brain;

namespace BotClient.Loop.Steps;

/// <summary>
/// The loop's craft step (WP-M3-12 placeholder; made real by WP-M3-6). At the forge it crafts from a mold in its Safe bag with the gathered and refined materials (the exit: a sword from T1 ingots and a grip material); argument: a <c>MoldDef</c> id.
/// <para>
/// Until then it is "not yet" (no forge protocol yet, WP-M3-6), logged once per run; the loop and a script go on. The WP replaces this file's body
/// (contract: Loop/Steps/README.md); the argument is the entry's (<c>"craft:&lt;argument&gt;"</c>), empty without one.
/// </para>
/// </summary>
public sealed class CraftStep : IBotStep
{
    private readonly string _argument;

    public CraftStep(string argument = "")
    {
        _argument = argument;
    }

    public string Id => "craft";

    public void Begin(BotStepContext context)
    {
    }

    public BotStepStatus Tick(BotStepContext context, ref BotCommand command) =>
        context.NotAvailable(Id, "no forge protocol yet, WP-M3-6" + (_argument.Length > 0 ? "; argument '" + _argument + "'" : ""));
}
