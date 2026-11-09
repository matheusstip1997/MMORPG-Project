using BotClient.Brain;

namespace BotClient.Loop.Steps;

/// <summary>
/// The pair's party (spec 6.5 R-6.5-01, M4 decision 1): <c>party:invite</c> (the bot invites its partner by display
/// name and is done on the committed <c>PartyState</c> holding both), <c>party:accept</c> (waits for the partner's
/// <c>PartyInvite</c> and accepts), <c>party:leave</c>, <c>party:chat</c> (a party line the partner sees). Both sides
/// meet at the point <c>party</c> first (<c>context.Pair</c>).
/// <para>
/// WP-M4-12 placeholder (made real by WP-M4-2): until then it is "not yet" (no party protocol yet), logged once per
/// run; the loop and a script go on.
/// The WP replaces this file's body (contract: Loop/Steps/README.md); the argument is the entry's
/// (<c>"party:&lt;argument&gt;"</c>), empty without one.
/// </para>
/// </summary>
public sealed class PartyStep : IBotStep
{
    private readonly string _argument;

    public PartyStep(string argument = "")
    {
        _argument = argument;
    }

    public string Id => "party";

    public void Begin(BotStepContext context)
    {
    }

    public BotStepStatus Tick(BotStepContext context, ref BotCommand command) =>
        context.NotAvailable(Id, "no party protocol yet, WP-M4-2" + (_argument.Length > 0 ? "; argument '" + _argument + "'" : ""));
}
