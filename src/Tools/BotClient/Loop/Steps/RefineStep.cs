using BotClient.Brain;
using Shared.Data.Tables;

namespace BotClient.Loop.Steps;

/// <summary>
/// The loop's refine step (WP-M3-6; spec 5.3 R-5.3-06, 11.5). At a station of its job it refines raw materials once (ore into
/// an ingot; the Jeweler's rough gem into a cut gem once WP-M3-9 adds its recipe): the first refine recipe in table order it
/// can make now, or the entry's (argument: a <c>RecipeDef</c> id, <c>refine:m3.refine.ingotT1</c>). One output per entry;
/// a script repeats the entry for more. The session is <see cref="ForgeRun"/>'s (counters <c>refine.made</c>,
/// <c>refine.noMaterials</c>, <c>refine.refused.&lt;reason&gt;</c>).
/// </summary>
public sealed class RefineStep : IBotStep
{
    private readonly ForgeRun _run;

    public RefineStep(string argument = "")
    {
        _run = new ForgeRun("refine", d => d.RecipeType == RecipeType.Refine && (argument.Length == 0 || d.Id == argument));
    }

    public string Id => "refine";

    public void Begin(BotStepContext context) => _run.Begin();

    public BotStepStatus Tick(BotStepContext context, ref BotCommand command) => _run.Tick(context, ref command);
}
