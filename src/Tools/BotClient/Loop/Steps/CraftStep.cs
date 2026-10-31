using BotClient.Brain;
using Shared.Protocol;
using Shared.Data.Tables;

namespace BotClient.Loop.Steps;

/// <summary>
/// The loop's craft step (WP-M3-6; spec 5.3 R-5.3-01..03, 11.5). At the forge it crafts once from a mold in its Safe bag with
/// the gathered and refined materials (the exit: a sword from T1 ingots and a hide grip): the first mold in table order it
/// can make now, or the entry's (argument: a <c>MoldDef</c> id, <c>craft:m3.moldSword</c>). It also plays the mold recipes
/// (a tool mold, <c>RecipeDef</c> type <c>mold</c>) when the argument names one. It uses each allowed forge action once. The
/// session is <see cref="ForgeRun"/>'s (counters <c>craft.made</c>, <c>craft.noMaterials</c>, <c>craft.refused.&lt;reason&gt;</c>).
/// </summary>
public sealed class CraftStep : IBotStep
{
    private readonly ForgeRun _run;

    public CraftStep(string argument = "")
    {
        _run = new ForgeRun("craft", d => argument.Length > 0
            ? d.Id == argument && (d.Kind == ForgeKind.Mold || d.RecipeType == RecipeType.Mold)
            : d.Kind == ForgeKind.Mold);
    }

    public string Id => "craft";

    public void Begin(BotStepContext context) => _run.Begin();

    public BotStepStatus Tick(BotStepContext context, ref BotCommand command) => _run.Tick(context, ref command);
}
