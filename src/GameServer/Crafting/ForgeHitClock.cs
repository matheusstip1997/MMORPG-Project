using Shared.Protocol;

namespace GameServer.Crafting;

/// <summary>
/// Whether a press on the forge timing bar came in time, on the server's timeline (spec 5.3 <c>IForgeChallenge</c>, M3
/// decision 12; the D4 clock-sync pattern of <c>DefenceTimeline</c>; WP-M3-10). The client times its press from when the
/// round reached it; the server knows when it issued the round, so the press's lag is the server's elapsed time minus the
/// client's offset, at least the round trip for an honest client:
/// <list type="bullet">
/// <item>a negative offset or lag claims more time than the server has seen: <see cref="ForgeHitOutcome.Impossible"/>
/// (D4's future claim);</item>
/// <item>an offset after the round, or a lag above the round trip plus the tolerance, or above the ceiling:
/// <see cref="ForgeHitOutcome.Late"/> (D4's too-old claim; without a round-trip sample only the ceiling applies);</item>
/// <item>otherwise <see cref="ForgeHitOutcome.None"/>: in time, scored by <c>TimingBar.Score</c>.</item>
/// </list>
/// The ceiling is the forge's own (<c>ForgeChallengeDef.maxLagMs</c>), not <c>lagCompensationCapMs</c> (user answer in the
/// WP-M3-10 plan, 2026-10-31: a forge input affects no other player). Pure, integer only.
/// </summary>
public static class ForgeHitClock
{
    public static ForgeHitOutcome Judge(long elapsedMs, long offsetMs, int roundTripMs, int toleranceMs, int maxLagMs, int durationMs,
        out long lagMs)
    {
        lagMs = elapsedMs - offsetMs;
        if (offsetMs < 0 || lagMs < 0)
            return ForgeHitOutcome.Impossible;
        if (offsetMs > durationMs)
            return ForgeHitOutcome.Late;
        long limit = roundTripMs < 0 ? maxLagMs : Math.Min(maxLagMs, (long)roundTripMs + toleranceMs);
        return lagMs > limit ? ForgeHitOutcome.Late : ForgeHitOutcome.None;
    }
}
