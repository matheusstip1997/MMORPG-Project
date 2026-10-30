using BotClient.Brain;

namespace BotClient.Loop.Steps;

/// <summary>
/// The loop's gather step (WP-M3-12 placeholder; made real by WP-M3-5). It walks to the nearest available resource node of its job (<c>context.Entities</c>, <c>EntityKind.ResourceNode</c>, WP-M3-4) whose tool it holds in the Safe bag equipment area, and harvests it (argument: a <c>NodeDef</c> id or tag to prefer).
/// <para>
/// Until then it is "not yet" (no harvest protocol yet, WP-M3-5), logged once per run; the loop and a script go on. The WP replaces this file's body
/// (contract: Loop/Steps/README.md); the argument is the entry's (<c>"gather:&lt;argument&gt;"</c>), empty without one.
/// </para>
/// </summary>
public sealed class GatherStep : IBotStep
{
    private readonly string _argument;

    public GatherStep(string argument = "")
    {
        _argument = argument;
    }

    public string Id => "gather";

    public void Begin(BotStepContext context)
    {
    }

    public BotStepStatus Tick(BotStepContext context, ref BotCommand command) =>
        context.NotAvailable(Id, "no harvest protocol yet, WP-M3-5" + (_argument.Length > 0 ? "; argument '" + _argument + "'" : ""));
}
