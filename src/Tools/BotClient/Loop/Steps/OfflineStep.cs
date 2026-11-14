using System.Globalization;
using BotClient.Brain;

namespace BotClient.Loop.Steps;

/// <summary>
/// A planned logout (WP-M4-8; spec 4.5, M4 decision 7: a stall sells while its owner is offline): <c>offline:&lt;seconds&gt;</c> posts
/// <c>offline</c> on the pair board, disconnects (neither lost nor "gone" for the partner), and the runner logs the bot in again with
/// a new ticket once the seconds are up; the loop then goes on with the next entry (a <c>mail:claim</c> of the proceeds). The
/// seconds must outlast the session linger, so the character leaves the world in between. Counter <c>offline.done</c>.
/// </summary>
public sealed class OfflineStep : IBotStep
{
    /// <summary>The pair board key posted just before the bot goes offline (read by the partner's <c>buy:stall:offline</c>).</summary>
    public const string OfflineKey = "offline";

    private readonly string _argument;
    private readonly int _seconds;
    private long _backAtMs;

    public OfflineStep(string argument = "")
    {
        _argument = argument;
        if (!int.TryParse(argument, NumberStyles.None, CultureInfo.InvariantCulture, out _seconds) || _seconds <= 0)
            _seconds = 0;
    }

    public string Id => "offline";

    public void Begin(BotStepContext context)
    {
        _backAtMs = 0;
    }

    public BotStepStatus Tick(BotStepContext context, ref BotCommand command)
    {
        if (_seconds <= 0)
            return context.Failed($"bad argument '{_argument}': offline:<seconds>");
        if (context.GoOffline is not { } goOffline)
            return context.NotAvailable(Id, "this bot cannot go offline");
        if (_backAtMs == 0)
        {
            if (context.InCombat)
                return BotStepStatus.Running; // a logout in combat lingers longer (R-2.1-08): wait it out first
            _backAtMs = context.NowMs + _seconds * 1_000L;
            context.Pair?.Post(OfflineKey, context.NowMs.ToString(CultureInfo.InvariantCulture));
            goOffline(_seconds * 1_000L);
            return BotStepStatus.Running;
        }
        if (context.NowMs < _backAtMs)
            return BotStepStatus.Running; // ticks run again only once the bot is back in the world
        context.Count("offline.done");
        return BotStepStatus.Done;
    }
}
