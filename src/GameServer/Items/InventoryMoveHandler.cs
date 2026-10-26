using GameServer.Entities;
using GameServer.Events;
using GameServer.Net;
using Shared.Protocol;
using Shared.Simulation.Items;

namespace GameServer.Items;

/// <summary>
/// Bag moves asked for by the inventory window (backlog G client part, WP16; spec 4.1, 9.13), in phase 1 on the world
/// thread. The client names two slots of its Normal bag; the move, merge or swap is <see cref="InventoryService.TryMove"/>
/// (the only writer of items, rule 4). A moved stack stays in the player's own bag, so it is save class 2 (R-7.1-01).
/// <list type="bullet">
/// <item>Operation key <c>move:{runId}:{entityId}:{connection}:{requestId}</c>: unique for ever (the database keeps every
/// key), and the same for a repeated request id on one connection, which gets the first answer and changes nothing
/// (rule 5, double submit).</item>
/// <item>A refusal is answered in the same tick's phase 10. An accepted move is answered only once the save that holds it
/// committed (rule 5: commit before confirming), right before the <see cref="InventoryContents"/> that shows it.</item>
/// <item>Several moves of one player in one tick run in arrival order, each on the bag the previous one left.</item>
/// </list>
/// Dead players may rearrange their bag: the spec has no rule against it.
/// </summary>
public sealed class InventoryMoveHandler
{
    private readonly InventoryService _inventory;
    private readonly string _runId;

    /// <param name="runId">Unique per world run, so operation keys never repeat across restarts.</param>
    public InventoryMoveHandler(InventoryService inventory, string runId)
    {
        _inventory = inventory;
        _runId = runId;
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
        var result = _inventory.TryMove(player.CharacterId, request.FromSlot, request.ToSlot, opKey, InventoryReason.PlayerMove);
        if (result == BagMoveResult.Refused)
        {
            // Nothing changed, so the bag still shows why.
            Refuse(player, request.RequestId, WhyRefused(_inventory.Current(player.CharacterId), request.FromSlot, request.ToSlot));
            return;
        }
        player.PendingMoveConfirmations.Add(new PendingMoveConfirmation(opKey, new InventoryMoveResult
        {
            RequestId = request.RequestId,
            Reason = InventoryMoveReason.None,
            Outcome = OutcomeOf(result),
        }));
    }

    public string OpKey(Player player, uint requestId) => $"move:{_runId}:{player.Id}:{player.Connection}:{requestId}";

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
