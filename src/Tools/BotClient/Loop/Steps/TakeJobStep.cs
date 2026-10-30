using BotClient.Brain;

namespace BotClient.Loop.Steps;

/// <summary>
/// The loop's takeJob step (WP-M3-12 placeholder; made real by WP-M3-1). It walks to the city job master and picks the entry's job (argument: a <c>JobDef</c> id, <c>takeJob:blacksmith</c>; without one the <c>JobDef</c> rows in id order, round robin by bot index) once the own level reaches the job's <c>minLevel</c>; below it, done with <c>takeJob.tooLow</c>. Reads the jobs from ClientCore's <c>ClientJobs</c> (<c>context.Jobs</c>, added by WP-M3-1).
/// <para>
/// Until then it is "not yet" (no job protocol yet, WP-M3-1), logged once per run; the loop and a script go on. The WP replaces this file's body
/// (contract: Loop/Steps/README.md); the argument is the entry's (<c>"takeJob:&lt;argument&gt;"</c>), empty without one.
/// </para>
/// </summary>
public sealed class TakeJobStep : IBotStep
{
    private readonly string _argument;

    public TakeJobStep(string argument = "")
    {
        _argument = argument;
    }

    public string Id => "takeJob";

    public void Begin(BotStepContext context)
    {
    }

    public BotStepStatus Tick(BotStepContext context, ref BotCommand command) =>
        context.NotAvailable(Id, "no job protocol yet, WP-M3-1" + (_argument.Length > 0 ? "; argument '" + _argument + "'" : ""));
}
