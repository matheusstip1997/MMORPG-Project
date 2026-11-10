namespace BotClient.Loop.Steps;

/// <summary>
/// The one place the WP-M4-2 steps read the bot's partner (spec 11.5): the name the partner posted on the pair board
/// (WP-M4-12, <c>context.Pair</c>: <c>BotScenario.pairs</c>, bot 2k with bot 2k+1; the QA partner bot's file board), the
/// display name an invite and a whisper use. Null while the bot plays alone or before the partner posted it. The party and
/// whisper steps need no meeting before they act: the server's state is their sync (the invitee waits for the
/// <c>PartyInvite</c>, the inviter for the committed <c>PartyState</c>); a step that must start together uses
/// <c>context.Pair.Arrive</c>/<c>Check</c> (Loop/Steps/README.md, "Pairs").
/// </summary>
internal static class PairPartner
{
    /// <summary>The partner's display name (the name an invite and a whisper use), or null when the bot plays alone.</summary>
    public static string? Name(BotStepContext context) => context.Pair?.PartnerName;
}
