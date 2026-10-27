using GameServer.Combat;
using Shared.Protocol;
using Shared.Simulation.Stats;

namespace GameServer.Progression;

/// <summary>The answer to a rank-up: <see cref="OutpostReason.None"/> and the new rank, or why not (and the rank kept).</summary>
public readonly record struct RankUpResult(OutpostReason Reason, Rank Rank)
{
    public bool Accepted => Reason == OutpostReason.None;
}

/// <summary>
/// The rank-up of an Adventurer Guild outpost (R-3.6-04). <see cref="RankUpInteraction"/> calls it for the outpost
/// interaction kind <c>rankUp</c> after the registry's gate (NPC, range), with the request id in the operation key. A
/// refusal is immediate (<c>RankRequirementsNotMet</c>, <c>RankMaxed</c>, or <c>InteractionUnavailable</c> without a held
/// character); an accepted rank-up is answered, and reaches the player as a <see cref="ProgressionState"/> with cause
/// RankUp, once its save committed (rule 5).
/// </summary>
public interface IRankUpHandler
{
    /// <param name="opKey">Idempotency key, unique for ever (for example <c>rankUp:{runId}:{entity}:{connection}:{requestId}</c>).</param>
    RankUpResult TryRankUp(long characterId, string opKey);

    /// <summary>True once the accepted rank-up <paramref name="opKey"/> is in a committed save.</summary>
    bool IsCommitted(long characterId, string opKey);
}

/// <summary>
/// Rank-up (R-3.6-04; spec 3.6 <c>RankService</c>, WP-M2-3), on the world thread. Not automatic: only the outpost
/// interaction calls it. The requirements of the character's current rank (<c>RankRequirements</c>, at least two
/// achievement categories) are checked against its counters as the world holds them now, unsaved ones included; they are
/// written by the same save as the rank-up, in one transaction with its <c>rank_history</c> audit row, so a crash keeps both
/// or neither (rule 5). A repeated key gets the first answer and changes nothing; a refusal is not recorded (asking again
/// later is a new check). The combatant's rank changes at once (damage steps 3 and 4); <c>RankChanged</c> is published
/// when the save commits.
/// </summary>
public sealed class RankService : IRankUpHandler
{
    private readonly ProgressionStore _store;
    private readonly Func<long, Combatant?> _combatantOf;

    /// <param name="combatantOf">The character's combatant in this world, if it has one.</param>
    public RankService(ProgressionStore store, Func<long, Combatant?> combatantOf)
    {
        _store = store;
        _combatantOf = combatantOf;
    }

    public RankUpResult TryRankUp(long characterId, string opKey)
    {
        if (_store.TryGetSeenRankOp(characterId, opKey, out var seen))
            return seen; // double submit: the first answer, nothing applied twice
        if (!_store.TryGetSnapshot(characterId, out var snapshot))
            return new RankUpResult(OutpostReason.InteractionUnavailable, Rank.F); // no progression held for it
        var rank = snapshot.Rank;
        var requirements = _store.Rules.RequirementsFrom(rank);
        if (rank >= Rank.SS || requirements.Count == 0)
            return new RankUpResult(OutpostReason.RankMaxed, rank);
        foreach (var requirement in requirements)
        {
            if (_store.CounterValue(characterId, requirement.Achievement) < requirement.Minimum)
                return new RankUpResult(OutpostReason.RankRequirementsNotMet, rank);
        }

        var to = _store.ApplyRankUp(characterId, opKey);
        if (_combatantOf(characterId) is { } combatant)
            combatant.Rank = to;
        var result = new RankUpResult(OutpostReason.None, to);
        _store.RememberRankOp(characterId, opKey, result);
        return result;
    }

    public bool IsCommitted(long characterId, string opKey) => _store.IsRankUpCommitted(characterId, opKey);
}
