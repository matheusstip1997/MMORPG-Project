using BotClient.Brain;

namespace BotClient.Loop.Steps;

/// <summary>
/// A whisper to the partner (spec 6.5, M4 decision 16): sends a whisper to the partner's display name
/// (<c>context.Pair.PartnerName</c>) and is done when the partner's whisper to this bot arrived (both sides play it).
/// <para>
/// WP-M4-12 placeholder (made real by WP-M4-2): until then it is "not yet" (no whisper protocol yet), logged once per
/// run; the loop and a script go on.
/// The WP replaces this file's body (contract: Loop/Steps/README.md); the argument is the entry's
/// (<c>"whisper:&lt;argument&gt;"</c>), empty without one.
/// </para>
/// </summary>
public sealed class WhisperStep : IBotStep
{
    private readonly string _argument;

    public WhisperStep(string argument = "")
    {
        _argument = argument;
    }

    public string Id => "whisper";

    public void Begin(BotStepContext context)
    {
    }

    public BotStepStatus Tick(BotStepContext context, ref BotCommand command) =>
        context.NotAvailable(Id, "no whisper protocol yet, WP-M4-2" + (_argument.Length > 0 ? "; argument '" + _argument + "'" : ""));
}
