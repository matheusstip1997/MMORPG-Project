using GameServer.Entities;
using GameServer.Outposts;
using Shared.Protocol;

namespace GameServer.Jobs;

/// <summary>
/// The NPC interaction kind <c>jobs</c> (spec 5.1 M3 decisions; WP-M3-1, on the WP-M2-4 registry): the job master opens the
/// Jobs window with its pick section. The registry's gate (NPC visible, offers jobs, in range) runs first; the answer is
/// <see cref="OutpostReason.None"/> at once and the player's <see cref="JobState"/> follows it in phase 10. The pick itself is a
/// <see cref="JobRequest"/>, which runs the same gate again (the window may stay open while the player walks away).
/// </summary>
public sealed class JobMasterInteraction : IOutpostInteractionHandler
{
    public OutpostReason? Interact(Player player, Npc npc, uint requestId)
    {
        player.Jobs.Mark(JobCause.Snapshot);
        return OutpostReason.None;
    }
}
