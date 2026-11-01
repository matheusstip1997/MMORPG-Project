using GameServer.Combat;
using GameServer.Crafting;
using GameServer.Entities;
using GameServer.Events;
using GameServer.Net;
using GameServer.Persistence;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Crafting;
using Shared.Simulation.Items;

namespace GameServer.World;

/// <summary>
/// Gems in the world (spec 5.3 R-5.3-05, 9.13; WP-M3-9): <see cref="GemService"/> sockets and removes gems; the world reads
/// the gem requests in phase 1 (the jeweler's bench gate, combat), announces a committed operation on the bus
/// (<c>GemSocketed</c>, <c>GemRemoved</c>) and to the player, and sends the answers in phase 10. Without the gem tables or
/// the item tables there is no service (worlds built for tests).
/// </summary>
public sealed partial class GameWorld
{
    private GemService? _gems;
    private GemSender? _gemSender;
    private int _gemCombatStateMs;

    /// <summary>Gem socketing and removal of the world's characters (WP-M3-9); null without the gem or item tables.</summary>
    public GemService? Gems => _gems;

    private void BuildGems(GemRules? rules, ulong rngSeed, int combatStateMs)
    {
        _gemSender = new GemSender(_transport);
        if (rules == null || _inventory == null)
            return;
        _gemCombatStateMs = combatStateMs;
        _gems = new GemService(rules, _inventory, _jobs, new LateJobBonuses(this), () => _clock.UtcNowMs, rngSeed)
        {
            OpCommitted = OnGemOpCommitted,
        };
    }

    /// <summary>Phase 1: one gem request of <paramref name="player"/>, at a jeweler's bench, out of combat (spec 5.3).</summary>
    private void HandleGemRequest(Player player, in ClientRequest request)
    {
        var op = (GemOp)request.FromSlot;
        if (_gems == null)
        {
            player.GemAnswers.Add(new GemResult { RequestId = request.RequestId, Op = op, Reason = GemReason.GemUnavailable });
            return;
        }
        bool inReach = StationOf(player, request.DropEntityId, out bool reach) == NpcInteraction.JewelerBench && reach;
        bool blocked = CombatState.IsInCombat(player.LastCombatAtMs, _clock.UtcNowMs, _gemCombatStateMs) || player.Actions.IsBusy;
        var ask = new GemAsk(request.RequestId, op, request.DropEntityId, inReach, blocked,
            new InventoryLocation(request.FromContainer, request.ToSlot), request.InstanceId, request.Count, request.ItemNetId,
            player.Connection);
        if (_gems.Handle(player.CharacterId, ask) is { } answer)
            player.GemAnswers.Add(answer);
    }

    /// <summary>A save committed a gem operation (rule 5): the bus, then the answer to the connection that asked.</summary>
    private void OnGemOpCommitted(long characterId, CommittedGemOp op)
    {
        _playersByCharacter.TryGetValue(characterId, out var player);
        uint entityId = player?.Id ?? 0;
        int region = player?.RegionIndex ?? 0;
        var record = op.Record;
        if (record.Kind == CraftOpKind.Socket)
            _events?.Publish(new GemSocketed(characterId, entityId, op.JobId, record.Tier, op.GemItemNetId, record.OutputInstanceId,
                record.Success, record.OpKey), characterId, region);
        else
            _events?.Publish(new GemRemoved(characterId, entityId, op.JobId, record.Tier, op.GemItemNetId, record.OutputInstanceId,
                record.OpKey), characterId, region);
        _events?.Publish(new GemCompleted(characterId, entityId, record.Kind, op.GemItemNetId, record.Tier, record.OutputInstanceId,
            record.Success, op.Answer.CostCount, record.Seed, record.OpKey), characterId, region);
        if (player == null || player.Connection != op.Connection)
            return;
        player.GemAnswers.Add(op.Answer);
    }
}
