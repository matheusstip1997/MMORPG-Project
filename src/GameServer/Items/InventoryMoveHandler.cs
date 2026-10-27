using GameServer.Combat;
using GameServer.Entities;
using GameServer.Events;
using GameServer.Net;
using Shared.Protocol;
using Shared.Simulation.Items;

namespace GameServer.Items;

/// <summary>
/// Inventory moves asked for by the inventory window (backlog G client part, WP16; containers and equipment WP-M2-1; spec
/// 4.1, 9.13), in phase 1 on the world thread. The client names two container slots; the move, merge, swap, equip or
/// unequip is <see cref="InventoryService.TryMove(long, InventoryLocation, InventoryLocation, string, InventoryReason)"/>
/// (the only writer of items, rule 4). Everything stays in the player's own containers, so it is save class 2 (R-7.1-01).
/// <list type="bullet">
/// <item>Operation key <c>move:{runId}:{entityId}:{connection}:{requestId}</c>: unique for ever (the database keeps every
/// key), and the same for a repeated request id on one connection, which gets the first answer and changes nothing
/// (rule 5, double submit).</item>
/// <item>Gear changes (into or out of Equipped) only out of combat (spec 12.7 combat state) and not during an action (any
/// action state but idle; spec 4.1 M2 item model, R-3.5-07): refused with <c>InCombat</c>.</item>
/// <item>A refusal is answered in the same tick's phase 10. An accepted move is answered only once the save that holds it
/// committed (rule 5: commit before confirming), right before the <see cref="InventoryContents"/> that shows it.</item>
/// <item>Several moves of one player in one tick run in arrival order, each on the containers the previous one left.</item>
/// </list>
/// Container code 0 is the Normal bag (the M1 request). Dead players may rearrange their bags (the spec has no rule
/// against it); a dead player's action state is not idle, so it cannot change gear.
/// </summary>
public sealed class InventoryMoveHandler
{
    private readonly InventoryService _inventory;
    private readonly string _runId;
    private readonly Func<long> _utcNowMs;

    /// <param name="runId">Unique per world run, so operation keys never repeat across restarts.</param>
    /// <param name="utcNowMs">The world clock (<c>IClock.UtcNowMs</c>), the one <c>Player.LastCombatAtMs</c> is in.</param>
    public InventoryMoveHandler(InventoryService inventory, string runId, Func<long>? utcNowMs = null)
    {
        _inventory = inventory;
        _runId = runId;
        _utcNowMs = utcNowMs ?? (() => 0);
    }

    /// <summary>Applies one move request of <paramref name="player"/> and queues its answer.</summary>
    public void Handle(Player player, in ClientRequest request)
    {
        if (!_inventory.IsAttached(player.CharacterId))
        {
            Refuse(player, request.RequestId, InventoryMoveReason.InventoryUnavailable);
            return;
        }
        string opKey = OpKey(player, request.RequestId);
        var from = Location(request.FromContainer, request.FromSlot);
        var to = Location(request.ToContainer, request.ToSlot);
        // A repeat of an applied request gets its first answer below, whatever the combat state is now (rule 5).
        if (_inventory.IsGearChange(from, to) && !_inventory.WasApplied(player.CharacterId, opKey)
            && (CombatState.IsInCombat(player.LastCombatAtMs, _utcNowMs(), _inventory.Rules.CombatStateMs) || player.Actions.IsBusy))
        {
            Refuse(player, request.RequestId, InventoryMoveReason.InCombat);
            return;
        }
        var result = _inventory.TryMove(player.CharacterId, from, to, opKey, InventoryReason.PlayerMove);
        if (!result.Applied)
        {
            Refuse(player, request.RequestId, result.Reason);
            return;
        }
        player.PendingMoveConfirmations.Add(new PendingMoveConfirmation(opKey, new InventoryMoveResult
        {
            RequestId = request.RequestId,
            Reason = InventoryMoveReason.None,
            Outcome = OutcomeOf(result.Result),
        }));
    }

    public string OpKey(Player player, uint requestId) => $"move:{_runId}:{player.Id}:{player.Connection}:{requestId}";

    private InventoryLocation Location(byte container, byte slot) =>
        new(container == 0 ? (byte)_inventory.Rules.NormalBag.Code : container, slot);

    /// <summary>The reason a move the bag refused: the same checks as <see cref="Bag.TryMove"/>, in its order.</summary>
    public static InventoryMoveReason WhyRefused(Bag bag, int from, int to)
    {
        if (from == to || from >= bag.SlotCount || to >= bag.SlotCount)
            return InventoryMoveReason.ItemSlotInvalid;
        if (bag[from].IsEmpty)
            return InventoryMoveReason.ItemSlotEmpty;
        return bag[to].SameKey(bag[from]) ? InventoryMoveReason.ItemStackFull : InventoryMoveReason.ItemSlotInvalid;
    }

    public static InventoryMoveOutcome OutcomeOf(BagMoveResult result) => result switch
    {
        BagMoveResult.Moved => InventoryMoveOutcome.Moved,
        BagMoveResult.Merged => InventoryMoveOutcome.Merged,
        BagMoveResult.Swapped => InventoryMoveOutcome.Swapped,
        _ => InventoryMoveOutcome.None,
    };

    private static void Refuse(Player player, uint requestId, InventoryMoveReason reason) =>
        player.MoveRefusals.Add(new InventoryMoveResult { RequestId = requestId, Reason = reason, Outcome = InventoryMoveOutcome.None });
}
