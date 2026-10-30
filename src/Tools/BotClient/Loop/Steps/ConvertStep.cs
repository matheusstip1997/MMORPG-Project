using BotClient.Brain;

namespace BotClient.Loop.Steps;

/// <summary>
/// The loop's convert step (WP-M3-12 placeholder; made real by WP-M3-6). At a station it converts a material tier T into T+1 (R-5.2-04; a failed conversion consumes the inputs); argument: a <c>RecipeDef</c> id.
/// <para>
/// Until then it is "not yet" (no forge protocol yet, WP-M3-6), logged once per run; the loop and a script go on. The WP replaces this file's body
/// (contract: Loop/Steps/README.md); the argument is the entry's (<c>"convert:&lt;argument&gt;"</c>), empty without one.
/// </para>
/// </summary>
public sealed class ConvertStep : IBotStep
{
    private readonly string _argument;

    public ConvertStep(string argument = "")
    {
        _argument = argument;
    }

    public string Id => "convert";

    public void Begin(BotStepContext context)
    {
    }

    public BotStepStatus Tick(BotStepContext context, ref BotCommand command) =>
        context.NotAvailable(Id, "no forge protocol yet, WP-M3-6" + (_argument.Length > 0 ? "; argument '" + _argument + "'" : ""));
}
