using GameServer.Entities;

namespace GameServer.Social;

/// <summary>
/// The one function that decides which name an observer sees for an entity (architecture rule 8, spec 6.2
/// DisguiseService): name plates, chat, and every other identity display go through it; no other code reads raw names for
/// display. M1 (backlog I) has no disguise, so a player shows its character name to everyone.
/// TODO(M2): a disguised player (robe, R-6.2-01..04) shows a generic name and no honor or red flag unless the observer
/// passes Sense vs Detect, or is in its party or guild.
/// Monsters have no server-side name: the client names them from <c>UiStrings</c> by their definition.
/// </summary>
public static class DisplayIdentity
{
    /// <summary>The name <paramref name="observer"/> sees for <paramref name="entity"/>; empty when it has none to send.</summary>
    public static string For(Entity entity, Player observer) => entity is Player player ? player.Name : string.Empty;
}
