namespace BotClient.Loop.Steps;

/// <summary>
/// The one place the WP-M4-2 steps read the bot's partner (spec 11.5; the pair plumbing is WP-M4-12's: <c>BotScenario.pairs</c>,
/// bot 2k with bot 2k+1, <c>context.Pair</c> with <c>IsFirst</c> and <c>PartnerName</c>). Until that lands no bot has a
/// partner and the pair steps report "partner needed"; when it lands only this member changes.
/// TODO(WP-M4-12): return <c>context.Pair?.PartnerName</c> (in place of <see cref="BotStepContext.PartnerName"/>) and use its
/// meeting (<c>Arrive</c>/<c>Check</c>) before an invite.
/// </summary>
internal static class PairPartner
{
    /// <summary>The partner's display name (the name an invite and a whisper use), or null when the bot plays alone.</summary>
    public static string? Name(BotStepContext context) => context.PartnerName;
}
