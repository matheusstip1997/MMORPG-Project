using GameServer.Combat;
using GameServer.Crafting;
using GameServer.Entities;
using GameServer.Events;
using GameServer.Net;
using Shared.Protocol;
using Shared.Simulation.Crafting;
using Shared.Simulation.Items;

namespace GameServer.World;

/// <summary>
/// Enchanting in the world (spec 5.3 R-5.3-04, M3 decision 10, 9.13; WP-M3-8): <see cref="EnchantService"/> runs every
/// character's attempts; the world reads the requests in phase 1 (the station gate of the forge, combat), and once an
/// attempt's save committed announces it on the bus (<see cref="Enchanted"/>; the stats follow through
/// <see cref="EquipmentChanged"/> from the inventory's commit) and answers the player. Without the enchant, forge or item
/// tables there are no attempts (worlds built for tests).
/// </summary>
public sealed partial class GameWorld
{
    private EnchantService? _enchant;

    /// <summary>The enchant attempts of the world's characters (WP-M3-8); null without the enchant or item tables.</summary>
    public EnchantService? Enchant => _enchant;

    /// <summary>The <c>craft_ops</c> producers the character saves take batches from (the forge, then enchanting).</summary>
    public Persistence.ICraftBatchSource? CraftBatches => new CraftBatchSources(_forge, _enchant).AsSource();

    private void BuildEnchant(EnchantRules? rules, ulong rngSeed)
    {
        if (rules == null || _inventory == null)
            return;
        _enchant = new EnchantService(rules, _inventory, _jobs, new LateJobBonuses(this), () => _clock.UtcNowMs, rngSeed)
        {
            OpCommitted = OnEnchantOpCommitted,
        };
    }

    /// <summary>Phase 1: one enchant request of <paramref name="player"/>; true when <paramref name="request"/> was one.</summary>
    private bool HandleEnchantRequest(Player player, in ClientRequest request)
    {
        if (request.Kind != ReliableMessageId.EnchantRequest)
            return false;
        if (_enchant == null)
        {
            player.Forge.Enchants.Add(new EnchantResult { RequestId = request.RequestId, Reason = EnchantReason.EnchantUnavailable });
            return true;
        }
        // Spec 5.3 M3 decision 7: at a station (the outpost gate and range), out of combat and not during an action.
        var station = StationOf(player, request.DropEntityId, out bool inReach);
        bool blocked = CombatState.IsInCombat(player.LastCombatAtMs, _clock.UtcNowMs, _forgeCombatStateMs) || player.Actions.IsBusy;
        var ask = new EnchantAsk(request.RequestId, request.DropEntityId, station,
            new InventoryLocation(request.FromContainer, request.FromSlot), request.InstanceId, inReach, blocked, player.Connection);
        if (_enchant.Request(player.CharacterId, ask) is { } answer)
            player.Forge.Enchants.Add(answer);
        return true;
    }

    /// <summary>A save committed an attempt (rule 5): the bus and the telemetry, then the answer to the connection that asked.</summary>
    private void OnEnchantOpCommitted(long characterId, CommittedEnchantOp op)
    {
        _playersByCharacter.TryGetValue(characterId, out var player);
        uint entityId = player?.Id ?? 0;
        int region = player?.RegionIndex ?? 0;
        var record = op.Record;
        // Enchanted.Tier is the level before (the record's contract): the job XP's difficulty factor (R-5.1-04).
        // The telemetry (kind 97, with the seed) comes from the event (TelemetryEventConsumer).
        _events?.Publish(new Enchanted(characterId, entityId, op.JobId, record.EnchantFrom, op.ItemNetId, record.OutputInstanceId,
            record.EnchantFrom, record.EnchantTo, record.OpKey, record.Seed, op.Stones), characterId, region);
        if (player == null || player.Connection != op.Connection)
            return;
        player.Forge.Enchants.Add(op.Answer);
    }
}
