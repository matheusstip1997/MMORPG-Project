using BotClient.Brain;

namespace BotClient.Loop.Steps;

/// <summary>
/// The loop's enchant step (WP-M3-12 placeholder; made real by WP-M3-8). At a station of the item's job it makes one enchant attempt on its equipped crafted weapon (R-5.3-04: +1, no change or -1; the stones are always consumed); argument: what to enchant.
/// <para>
/// Until then it is "not yet" (no enchant protocol yet, WP-M3-8), logged once per run; the loop and a script go on. The WP replaces this file's body
/// (contract: Loop/Steps/README.md); the argument is the entry's (<c>"enchant:&lt;argument&gt;"</c>), empty without one.
/// </para>
/// </summary>
public sealed class EnchantStep : IBotStep
{
    private readonly string _argument;

    public EnchantStep(string argument = "")
    {
        _argument = argument;
    }

    public string Id => "enchant";

    public void Begin(BotStepContext context)
    {
    }

    public BotStepStatus Tick(BotStepContext context, ref BotCommand command) =>
        context.NotAvailable(Id, "no enchant protocol yet, WP-M3-8" + (_argument.Length > 0 ? "; argument '" + _argument + "'" : ""));
}
