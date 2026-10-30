using BotClient.Brain;

namespace BotClient.Loop.Steps;

/// <summary>
/// The loop's refine step (WP-M3-12 placeholder; made real by WP-M3-6). At a station of its job it refines raw materials (ore into ingots; the Jeweler's rough gem into a cut gem, WP-M3-9); argument: a <c>RecipeDef</c> id.
/// <para>
/// Until then it is "not yet" (no forge protocol yet, WP-M3-6 (gems: WP-M3-9)), logged once per run; the loop and a script go on. The WP replaces this file's body
/// (contract: Loop/Steps/README.md); the argument is the entry's (<c>"refine:&lt;argument&gt;"</c>), empty without one.
/// </para>
/// </summary>
public sealed class RefineStep : IBotStep
{
    private readonly string _argument;

    public RefineStep(string argument = "")
    {
        _argument = argument;
    }

    public string Id => "refine";

    public void Begin(BotStepContext context)
    {
    }

    public BotStepStatus Tick(BotStepContext context, ref BotCommand command) =>
        context.NotAvailable(Id, "no forge protocol yet, WP-M3-6 (gems: WP-M3-9)" + (_argument.Length > 0 ? "; argument '" + _argument + "'" : ""));
}
