using GameServer.Combat;
using GameServer.Entities;
using GameServer.Events;
using GameServer.Persistence;
using Shared.Protocol;
using Shared.Simulation.Stats;

namespace GameServer.World;

/// <summary>
/// Free attribute points in the world (spec 3.4 R-3.4-08, 9.13; WP-M2-13): a player's <see cref="AttributeSpendRequest"/>
/// goes to <see cref="Progression.ProgressionStore.PlaceAttribute"/> (the only writer of the points, rule 4) with the
/// world's facts (in combat or acting, the attribute's base and <c>StatDef</c> max); a refusal is answered at once, a
/// placement once its save committed (rule 5), when the bus hears <see cref="AttributesChanged"/> (stats rebuild on it)
/// and the client gets the answer and then its <see cref="ProgressionState"/>.
/// </summary>
public sealed partial class GameWorld
{
    private string _attributeRunId = string.Empty;
    private int _attributeCombatStateMs;

    /// <summary>Placements waiting for their save, by op key: who asked, on which connection, so the commit can answer.</summary>
    private readonly Dictionary<string, AttributeWait> _attributeWaits = new(StringComparer.Ordinal);

    private readonly record struct AttributeWait(uint RequestId, byte Attribute, int Connection);

    private void BuildAttributes(string runId, int combatStateMs)
    {
        _attributeRunId = runId;
        _attributeCombatStateMs = combatStateMs;
        if (_progression != null)
            _progression.AttributeOpCommitted = OnAttributeOpCommitted;
    }

    /// <summary>Phase 1: one attribute point request of <paramref name="player"/>, answered now or once its save committed.</summary>
    private void HandleAttributeRequest(Player player, in Net.ClientRequest request)
    {
        byte attribute = request.ToSlot;
        string opKey = AttributeOpKey(player, request.RequestId);
        // Blocked: in combat (instances and boss fights are combat; TODO(M4) instances) or during an action, dead included
        // (user decision 2026-10-29, like evolve and respec).
        bool blocked = CombatState.IsInCombat(player.LastCombatAtMs, _clock.UtcNowMs, _attributeCombatStateMs) || player.Actions.IsBusy;
        int baseValue = 0;
        int statMax = int.MaxValue;
        if (player.Combatant?.Stats is { Formula: { } formula } stats && attribute < Attributes.Count)
        {
            int statId = formula.AttributeStat(attribute);
            baseValue = stats.GetBase(statId);
            statMax = stats.Rules.Max(statId);
        }
        var answer = _progression == null
            ? AttributeReason.AttributeUnavailable
            : _progression.PlaceAttribute(player.CharacterId, attribute, opKey, blocked, baseValue, statMax);
        if (answer is { } reason)
        {
            player.AttributeAnswers.Add(new AttributeSpendResult { RequestId = request.RequestId, Attribute = attribute, Reason = reason });
            return;
        }
        _attributeWaits.TryAdd(opKey, new AttributeWait(request.RequestId, attribute, player.Connection));
    }

    /// <summary>The idempotency key of an attribute request: one per world run, entity, connection and request id (spec 9.13 pattern).</summary>
    internal string AttributeOpKey(Player player, uint requestId) => $"attr:{_attributeRunId}:{player.Id}:{player.Connection}:{requestId}";

    /// <summary>A save committed a placement or a reset (rule 5): the bus, the client's progression, the answer.</summary>
    private void OnAttributeOpCommitted(long characterId, AttributeOpRecord op)
    {
        _playersByCharacter.TryGetValue(characterId, out var player);
        int unspent = _progression != null && _progression.TryGetAttributes(characterId, out var attributes) ? attributes.Unspent : 0;
        _events?.Publish(new AttributesChanged(characterId, player?.Id ?? 0, op.Attribute, op.Points, unspent), characterId,
            player?.RegionIndex ?? 0);
        OnProgressionChanged(characterId, ProgressionCause.AttributePoints);
        if (!_attributeWaits.Remove(op.OpKey, out var wait))
            return; // a reset (answered as the respec), or a placement of a world run before a restart
        if (player != null && player.Connection == wait.Connection)
            player.AttributeAnswers.Add(new AttributeSpendResult { RequestId = wait.RequestId, Attribute = wait.Attribute, Reason = AttributeReason.None });
    }
}
