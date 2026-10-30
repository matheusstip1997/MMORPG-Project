using GameServer.Combat;
using GameServer.Entities;
using GameServer.Events;
using GameServer.Net;
using Shared.Protocol;
using Shared.Simulation.Actions;
using Shared.Simulation.Items;

namespace GameServer.Items;

/// <summary>
/// Using a consumable (spec 3.4 "Consumable use", R-3.4-03, R-3.7-05; M3 plan decision 3, WP-M3-3): using an item is an
/// action, allowed in combat, so drinking in a fight is a commitment.
/// <list type="number">
/// <item>Phase 1, <see cref="Handle"/>: a <see cref="UseItemRequest"/> is checked (inventory loaded, alive, the slot holds a
/// consumable with a <c>ConsumableDef</c> row, not reserved, no use in progress, its cooldown over, the meters allow it:
/// <see cref="ConsumableRules.Check"/>) and becomes the player's <see cref="PendingUse"/>; a refusal is answered in the same
/// tick's phase 10.</item>
/// <item>Phase 3, <see cref="RequestedAction"/> and <see cref="AfterStep"/>: the <see cref="ActionRunner"/> asks the shared
/// action machine for the item's use action (<see cref="ActionInput.WithRequest"/>): it starts only from Idle, through the
/// machine's own checks. Not started: refused (<c>ActionBusy</c>, or the machine's reason). Cancelled (a dodge) or interrupted
/// (flinch, stagger, stun, death) before its consume effect: <c>UseInterrupted</c>, nothing used.</item>
/// <item>Phase 3, <see cref="OnEffectPoint"/>, when the consume effect fires: the slot must still hold the item and the
/// meters still allow it; then <see cref="InventoryService.TryRemove(long, int, int, int, string, InventoryReason, string)"/>
/// removes one unit (reason <c>ItemUsed</c>, save class 2, R-7.1-01; operation key
/// <c>use:{runId}:{entityId}:{connection}:{requestId}</c>), the toxicity or satiety rises (in the same save as the removal),
/// the cooldown starts, and the heal, MP restore and status are queued for phase 6 (<see cref="StatusSystem"/>).</item>
/// <item>Phase 10: the use is answered only once the save holding the removal committed (rule 5), right before the
/// <see cref="InventoryContents"/> that shows it; <see cref="InventoryService"/> publishes <see cref="ItemUsed"/> on that
/// commit.</item>
/// </list>
/// A repeated request id on one connection gets the first answer and uses nothing more (double submit). World thread only;
/// allocation-free per tick except the operation key of an accepted request.
/// </summary>
internal sealed class ConsumableUse
{
    private readonly InventoryService _inventory;
    private readonly ConsumableCatalog _catalog;
    private readonly CombatRules _combat;
    private readonly string _runId;
    private readonly Func<long> _utcNowMs;

    /// <param name="runId">Unique per world run, so operation keys never repeat across restarts.</param>
    /// <param name="utcNowMs">The meters' clock (<c>IClock.UtcNowMs</c>, R-3.4-03).</param>
    public ConsumableUse(InventoryService inventory, ConsumableCatalog catalog, CombatRules combat, string runId,
        Func<long> utcNowMs)
    {
        _inventory = inventory;
        _catalog = catalog;
        _combat = combat;
        _runId = runId;
        _utcNowMs = utcNowMs;
    }

    /// <summary>Where the heal, MP restore and status go (phase 6); null: they do nothing (a world without statuses).</summary>
    public StatusSystem? Statuses { get; set; }

    public ConsumableCatalog Catalog => _catalog;

    public string OpKey(Player player, uint requestId) => $"use:{_runId}:{player.Id}:{player.Connection}:{requestId}";

    /// <summary>Phase 1: checks one use request of <paramref name="player"/> and makes it its pending use, or refuses it.</summary>
    public void Handle(Player player, in ClientRequest request, uint tick)
    {
        var uses = player.Uses;
        uint requestId = request.RequestId;
        long characterId = player.CharacterId;
        if (!_inventory.IsAttached(characterId))
        {
            Refuse(player, requestId, 0, UseItemReason.InventoryUnavailable);
            return;
        }
        if (uses.Current is { } current && current.RequestId == requestId)
            return; // a double submit while the first runs: the first one answers
        string opKey = OpKey(player, requestId);
        if (_inventory.WasApplied(characterId, opKey))
        {
            // Rule 5, double submit: the first answer again (after the commit), nothing used.
            uses.Confirmations.Add(new PendingUseConfirmation(opKey, Used(requestId, uses.UsedItem(opKey))));
            return;
        }
        if (player.Combatant is not { IsAlive: true })
        {
            Refuse(player, requestId, 0, UseItemReason.UseWhileDead);
            return;
        }
        byte container = request.FromContainer == 0 ? (byte)_inventory.Rules.NormalBag.Code : request.FromContainer;
        var items = _inventory.CurrentInventory(characterId);
        if (!items.TryIndex(container, out int index) || request.FromSlot >= items.BagAt(index).SlotCount)
        {
            Refuse(player, requestId, 0, UseItemReason.ItemSlotInvalid);
            return;
        }
        var stack = items.BagAt(index)[request.FromSlot];
        if (stack.IsEmpty)
        {
            Refuse(player, requestId, 0, UseItemReason.ItemSlotEmpty);
            return;
        }
        if (!_catalog.TryGet(stack.ItemNetId, out var entry))
        {
            Refuse(player, requestId, stack.ItemNetId, UseItemReason.ItemNotUsable);
            return;
        }
        // Reserved units (crafting, a contract; WP-M3-2's reservations) cannot be used.
        if (_inventory.Unreserved(characterId, new InventoryLocation(container, request.FromSlot)) < 1)
        {
            Refuse(player, requestId, entry.ItemNetId, UseItemReason.ItemReserved);
            return;
        }
        if (uses.Current != null)
        {
            Refuse(player, requestId, entry.ItemNetId, UseItemReason.UseInProgress);
            return;
        }
        if (tick < uses.ReadyTick(entry.Index))
        {
            Refuse(player, requestId, entry.ItemNetId, UseItemReason.OnCooldown);
            return;
        }
        var meters = MetersCheck(player, entry);
        if (meters != UseItemReason.None)
        {
            Refuse(player, requestId, entry.ItemNetId, meters);
            return;
        }
        uses.Current = new PendingUse(requestId, opKey, container, request.FromSlot, entry);
    }

    /// <summary>Phase 3, before the machine steps: the catalog index of the use action asked for this tick, or -1.</summary>
    public int RequestedAction(Player player) =>
        player.Uses.Current is { State: PendingUseState.Requested } use ? use.Entry.ActionIndex : -1;

    /// <summary>
    /// Phase 3, after the machine stepped (before its effects are handed out): a requested use started or was refused; a
    /// running use whose action is no longer running (cancelled or interrupted before its effect point) used nothing.
    /// </summary>
    /// <returns>True when the machine's refusal of this step was the use's (the caller sends no <c>ActionResult</c> for it).</returns>
    public bool AfterStep(Player player, in ActionStepResult result)
    {
        var uses = player.Uses;
        if (uses.Current is not { } use)
            return false;
        int action = use.Entry.ActionIndex;
        if (use.State == PendingUseState.Requested)
        {
            if (result.StartedIndex == action)
            {
                use.State = PendingUseState.Running;
                return false;
            }
            bool machineRefused = result.RefusedIndex == action && result.Refused != ActionReason.None;
            Refuse(player, use.RequestId, use.Entry.ItemNetId,
                machineRefused && result.Refused == ActionReason.OnCooldown ? UseItemReason.OnCooldown : UseItemReason.ActionBusy);
            uses.Current = null;
            return machineRefused;
        }
        if (player.Actions.ActionIndex != action)
        {
            Refuse(player, use.RequestId, use.Entry.ItemNetId, UseItemReason.UseInterrupted);
            uses.Current = null;
        }
        return false;
    }

    /// <summary>
    /// Phase 3: the use action's consume effect fired. Removes one unit and applies the consumable (see the class remarks);
    /// a use that no longer holds (the slot changed, the meters filled up) is answered with its reason and uses nothing.
    /// </summary>
    public void OnEffectPoint(Player player, uint tick)
    {
        var uses = player.Uses;
        if (uses.Current is not { State: PendingUseState.Running } use)
            return; // the client left (unbind) after the action started: nothing is used
        uses.Current = null;
        var entry = use.Entry;
        long characterId = player.CharacterId;
        if (!_inventory.IsAttached(characterId))
        {
            Refuse(player, use.RequestId, entry.ItemNetId, UseItemReason.InventoryUnavailable);
            return;
        }
        var items = _inventory.CurrentInventory(characterId);
        if (!items.TryIndex(use.Container, out int index) || items.BagAt(index)[use.Slot].ItemNetId != entry.ItemNetId
            || items.BagAt(index)[use.Slot].IsEmpty)
        {
            Refuse(player, use.RequestId, entry.ItemNetId, UseItemReason.ItemSlotEmpty);
            return;
        }
        if (_inventory.Unreserved(characterId, new InventoryLocation(use.Container, use.Slot)) < 1)
        {
            Refuse(player, use.RequestId, entry.ItemNetId, UseItemReason.ItemReserved);
            return;
        }
        var meters = MetersCheck(player, entry);
        if (meters != UseItemReason.None)
        {
            Refuse(player, use.RequestId, entry.ItemNetId, meters);
            return;
        }
        if (!_inventory.TryRemove(characterId, use.Container, use.Slot, 1, use.OpKey, InventoryReason.ItemUsed, "use"))
        {
            Refuse(player, use.RequestId, entry.ItemNetId, UseItemReason.ItemSlotEmpty);
            return;
        }
        // R-3.4-03: the meters ride the same character save as the removal (SessionManager captures both in one job).
        if (player.Consumption is { } consumption)
        {
            long now = _utcNowMs();
            consumption.AddToxicity(entry.Def.Toxicity, now);
            consumption.AddSatiety(entry.Def.Satiety, now);
            player.StatsMessageDue = true;
        }
        if (entry.CooldownTicks > 0)
            uses.StartCooldown(entry.Index, _catalog.Count, tick + (uint)entry.CooldownTicks);
        Statuses?.QueueConsumable(player, entry);
        uses.RecordUsed(use.OpKey, entry.ItemNetId);
        uses.Confirmations.Add(new PendingUseConfirmation(use.OpKey, Used(use.RequestId, entry.ItemNetId)));
    }

    private UseItemReason MetersCheck(Player player, ConsumableEntry entry)
    {
        if (player.Consumption is not { } consumption)
            return UseItemReason.None;
        long now = _utcNowMs();
        return ConsumableRules.Check(entry.Def, consumption.ToxicityMilli(now), consumption.SatietyMilli(now), _combat.Consumption);
    }

    private static UseItemResult Used(uint requestId, ushort itemNetId) =>
        new() { RequestId = requestId, ItemNetId = itemNetId, Reason = UseItemReason.None };

    private static void Refuse(Player player, uint requestId, ushort itemNetId, UseItemReason reason) =>
        player.Uses.Refusals.Add(new UseItemResult { RequestId = requestId, ItemNetId = itemNetId, Reason = reason });
}
