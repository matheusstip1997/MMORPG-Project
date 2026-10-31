using BotClient.Brain;
using Shared.Data.Tables;

namespace BotClient.Loop.Steps;

/// <summary>
/// The loop's convert step (WP-M3-6; spec 5.2 R-5.2-04, 11.5). At a station it converts a material tier T into T+1 once: the
/// first conversion recipe in table order it can make now, or the entry's (argument: a <c>RecipeDef</c> id). A failed
/// conversion consumes the inputs and is still done (<c>convert.convertFailed</c>); the daily cap refuses with
/// <c>ConversionCapReached</c>. The session is <see cref="ForgeRun"/>'s (counters <c>convert.made</c>, <c>convert.noMaterials</c>,
/// <c>convert.refused.&lt;reason&gt;</c>).
/// </summary>
public sealed class ConvertStep : IBotStep
{
    private readonly ForgeRun _run;

    public ConvertStep(string argument = "")
    {
        _run = new ForgeRun("convert", d => d.RecipeType == RecipeType.Convert && (argument.Length == 0 || d.Id == argument));
    }

    public string Id => "convert";

    public void Begin(BotStepContext context) => _run.Begin();

    public BotStepStatus Tick(BotStepContext context, ref BotCommand command) => _run.Tick(context, ref command);
}
