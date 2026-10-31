using GameServer.Entities;
using GameServer.Outposts;
using Shared.Protocol;

namespace GameServer.Crafting;

/// <summary>
/// The NPC interaction kinds <c>forge</c> and <c>jewelerBench</c> (spec 5.3 M3 decisions 6 and 7; WP-M3-6, on the WP-M2-4
/// registry): a public station opens the forge window. The registry's gate (station visible, offers the kind, in range)
/// runs first; the answer is <see cref="OutpostReason.None"/> at once, with the character's session state (the window shows
/// a session left running). Every forge request runs the same gate again (the window may stay open while the player walks
/// away). TODO(open): a player-owned place to forge (an own shop) comes later (user note 2026-10-30, with the shops of 4.2).
/// </summary>
public sealed class ForgeStationInteraction(ForgeService forge) : IOutpostInteractionHandler
{
    public OutpostReason? Interact(Player player, Npc npc, uint requestId)
    {
        player.Forge.States.Add(forge.State(player.CharacterId, 0, ForgeReason.None));
        return OutpostReason.None;
    }
}
