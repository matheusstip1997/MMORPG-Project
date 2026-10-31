using GameServer.Entities;
using GameServer.Events;
using GameServer.Net;
using GameServer.World.Nodes;
using Shared.Protocol;
using Shared.Util;

namespace GameServer.World;

/// <summary>
/// Harvesting in the world (spec 5.2, WP-M3-5): <see cref="HarvestService"/> reads the harvest requests in phase 1, runs the
/// harvest action through the <see cref="Combat.ActionRunner"/> in phase 3 (the yield added and the node depleted at its
/// effect point), hears damage from the bus (phase 11) and announces committed harvests (<c>Harvested</c>). Its yields roll
/// from their own seeded stream of the world seed (rule 6), logged by the host. Without the nodes, the items, the job query
/// or the harvest tables nothing can be harvested (worlds built for tests).
/// </summary>
public sealed partial class GameWorld
{
    /// <summary>Added to the crit stream for the harvest generator's stream (same seed, independent sequence).</summary>
    public const ulong HarvestStreamOffset = 0x48415256; // "HARV"

    private HarvestService? _harvests;

    /// <summary>Stream of the harvest generator (seed <see cref="RngSeed"/>); logged by the host. 0 without harvesting.</summary>
    public ulong HarvestRngStream { get; private set; }

    /// <summary>Harvesting (WP-M3-5); null when the world has no nodes, items or harvest tables.</summary>
    internal HarvestService? Harvests => _harvests;

    private void BuildHarvest(NodeRules nodes, EventBus? events, string runId, ulong rngSeed, ulong rngStream)
    {
        if (_nodes == null || _inventory == null || nodes.Harvest is not { } settings)
            return;
        var catalog = HarvestCatalog.From(nodes, _actions);
        if (catalog.Count == 0)
            return;
        HarvestRngStream = rngStream + HarvestStreamOffset;
        _harvests = new HarvestService(_inventory, catalog, _nodes, settings, _jobs, new Pcg32Rng(rngSeed, HarvestRngStream),
            HarvestRngStream, runId, TryDepleteNode, _findEntity,
            id => _playersByCharacter.TryGetValue(id, out var player) ? player : null, events) { Bonuses = JobBonusQuery };
        JobActivityGates.Add(_harvests); // a respec of the job tree waits while a harvest runs (WP-M3-7)
        _inventory.OpCommitted = _harvests.OnOpCommitted;
        _actionRunner.Harvests = _harvests;
        if (events != null)
            _harvests.Subscribe(events);
    }

    /// <summary>Phase 1: one harvest request of <paramref name="player"/>; the harvest action starts in phase 3.</summary>
    private void HandleHarvestRequest(Player player, in ClientRequest request)
    {
        if (_harvests != null)
            _harvests.Handle(player, request);
        else
            player.Harvests.Refusals.Add(new HarvestResult
            {
                RequestId = request.RequestId, NodeEntityId = request.DropEntityId, Reason = HarvestReason.InventoryUnavailable,
            });
    }
}
