using GameServer.Entities;
using GameServer.Outposts;
using Shared.Data.Tables;
using Shared.Protocol;

namespace GameServer.Progression;

/// <summary>
/// The outpost interaction kind <c>rankUp</c> (R-3.6-04; WP-M2-3 on the WP-M2-4 registry), on the world thread. The
/// registry's gate (NPC visible, in <c>interactRangeCm</c>, the NPC offers rankUp) runs first; the outpost the player stands
/// at counts as visited before the check (the <c>OutpostVisited</c> event is only counted in phase 11). A refusal is
/// answered at once; an accepted rank-up is answered with <see cref="OutpostReason.None"/> only when <see cref="Committed"/> reports
/// its save (rule 5). Operation key <c>rankUp:{runId}:{entityId}:{connection}:{requestId}</c>: unique for ever, and the
/// same for a repeated request on one connection, which gets the first answer and changes nothing (double submit).
/// </summary>
public sealed class RankUpInteraction : IOutpostInteractionHandler
{
    private readonly IRankUpHandler _ranks;
    private readonly AchievementService? _achievements;
    private readonly string _runId;
    private readonly Dictionary<string, Waiting> _waiting = new(StringComparer.Ordinal);

    /// <param name="runId">Unique per world run, so operation keys never repeat across restarts.</param>
    public RankUpInteraction(IRankUpHandler ranks, AchievementService? achievements, string runId)
    {
        _ranks = ranks;
        _achievements = achievements;
        _runId = runId;
    }

    /// <summary>Accepted rank-ups waiting for their save.</summary>
    public int WaitingCount => _waiting.Count;

    public string OpKey(Player player, uint requestId) => $"rankUp:{_runId}:{player.Id}:{player.Connection}:{requestId}";

    public OutpostReason? Interact(Player player, Npc npc, uint requestId)
    {
        _achievements?.RecordOutpostVisit(player.CharacterId, npc);
        string opKey = OpKey(player, requestId);
        var result = _ranks.TryRankUp(player.CharacterId, opKey);
        if (!result.Accepted)
            return result.Reason;
        if (_ranks.IsCommitted(player.CharacterId, opKey))
            return OutpostReason.None; // a repeat after the commit: the first answer again
        _waiting.TryAdd(opKey, new Waiting(player.CharacterId, player.Connection, new OutpostInteractResult
        {
            RequestId = requestId,
            NpcEntityId = npc.Id,
            Kind = NpcInteraction.RankUp,
            Reason = OutpostReason.None,
        }));
        return null; // answered by Committed
    }

    /// <summary>
    /// A save committed the rank-up <paramref name="opKey"/>: answer the request, if the connection that asked is still
    /// the player's (a new connection gets the rank in its <see cref="ProgressionState"/>).
    /// </summary>
    public void Committed(Player? player, string opKey)
    {
        if (!_waiting.Remove(opKey, out var waiting))
            return;
        if (player != null && player.CharacterId == waiting.CharacterId && player.Connection == waiting.Connection)
            player.Outposts.Interactions.Add(waiting.Answer);
    }

    /// <summary>The character left the world: its unanswered rank-ups have no one to answer.</summary>
    public void Forget(long characterId)
    {
        if (_waiting.Count == 0)
            return;
        foreach (var (key, waiting) in _waiting.ToArray())
        {
            if (waiting.CharacterId == characterId)
                _waiting.Remove(key);
        }
    }

    private readonly record struct Waiting(long CharacterId, int Connection, OutpostInteractResult Answer);
}
