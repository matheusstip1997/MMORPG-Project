using GameServer.Events;
using GameServer.Persistence;
using Shared.Simulation.Items;

namespace GameServer.Items;

/// <summary>
/// Changes to an instance's typed properties (spec 4.1 M3 item model; the enchant level of WP-M3-8, the socketed gems of
/// WP-M3-9). A change is an audited, idempotent operation like any other item change (rule 4: only this service writes
/// items), saved in the character's batch.
/// <para>
/// <b>Committed properties only are shown</b> (rule 5): the client's contents (<c>InventorySender</c>), the stats
/// (<see cref="IEquipmentStatSource"/>) and <see cref="TryGetProperties"/> read the committed object. A change lives in a
/// pending clone until the save that carries it commits; then it replaces the committed object and, for an equipped
/// instance, rebuilds the gear and publishes <see cref="EquipmentChanged"/>. So a crash before the commit shows nothing, and
/// a reservation change (which resends the contents at once) cannot show a level that was never saved.
/// </para>
/// </summary>
public sealed partial class InventoryService
{
    /// <summary>
    /// Changes the properties of the instance at <paramref name="at"/> under <paramref name="opKey"/>: <paramref name="change"/>
    /// edits a clone of its latest properties (pending or committed). A repeated key returns the first result and changes
    /// nothing; false, and nothing recorded, for an empty slot or a stack, or when the result names stats or gems the tables
    /// do not know.
    /// </summary>
    public bool TryChangeProperties(long characterId, InventoryLocation at, Action<ItemProperties> change, string opKey,
        InventoryReason reason, string source)
    {
        var inventory = Get(characterId);
        if (inventory.SeenOps.TryGetValue(opKey, out var previous))
            return previous > 0;
        if (!inventory.Items.TryIndex(at.Container, out int index) || at.Slot < 0 || at.Slot >= inventory.Items.BagAt(index).SlotCount)
            return false;
        var stack = inventory.Items.BagAt(index)[at.Slot];
        if (!stack.IsInstance || !inventory.Instances.ContainsKey(stack.InstanceId))
            return false;
        var next = LatestProperties(inventory, stack.InstanceId).Clone();
        change(next);
        if (!_rules.Represents(next))
            return false;
        inventory.PendingProperties[stack.InstanceId] = next;
        inventory.SeenOps.Add(opKey, 1);
        inventory.PendingOps.Add(new InventoryOpRecord(opKey, reason.ToString(), source));
        inventory.PendingAudit.Add(Audit(opKey, 0, AuditKind.Modify, at.Container, at.Slot, stack, 0, stack.Count));
        inventory.PendingInstances.Add(stack.InstanceId);
        MarkClass2?.Invoke(characterId);
        return true;
    }

    /// <summary>
    /// The newest properties of an instance the character holds, a change not yet committed included: what the next change
    /// builds on. Read-only, for the owner of the change (the enchant roll reads the level it starts from); never shown.
    /// </summary>
    public bool TryGetLatestProperties(long characterId, long instanceId, out ItemProperties properties)
    {
        properties = null!;
        if (!_characters.TryGetValue(characterId, out var inventory) || !inventory.Instances.ContainsKey(instanceId))
            return false;
        properties = LatestProperties(inventory, instanceId);
        return true;
    }

    private static ItemProperties LatestProperties(CharacterInventory inventory, long instanceId)
    {
        if (inventory.PendingProperties.TryGetValue(instanceId, out var pending))
            return pending;
        if (inventory.InFlight?.Properties is { } inFlight && inFlight.TryGetValue(instanceId, out var saving))
            return saving;
        return inventory.Instances[instanceId].Properties;
    }

    /// <summary>The properties a batch writes for an instance: its pending change, else the committed object.</summary>
    private static ItemProperties PropertiesToWrite(CharacterInventory inventory, long instanceId, ItemProperties committed) =>
        inventory.PendingProperties.TryGetValue(instanceId, out var pending) ? pending : committed;

    /// <summary>Moves the pending changes into the batch being issued (they commit with it).</summary>
    private static Dictionary<long, ItemProperties>? TakePendingProperties(CharacterInventory inventory)
    {
        if (inventory.PendingProperties.Count == 0)
            return null;
        var taken = new Dictionary<long, ItemProperties>(inventory.PendingProperties);
        inventory.PendingProperties.Clear();
        return taken;
    }

    /// <summary>
    /// A committed batch's property changes become the committed properties; an equipped instance's slot joins
    /// <see cref="_changedSlots"/>, so the gear is rebuilt and <see cref="EquipmentChanged"/> published (WP-M3-8).
    /// </summary>
    private void CommitProperties(CharacterInventory inventory, InFlightBatch inFlight)
    {
        if (inFlight.Properties == null)
            return;
        foreach (var (instanceId, properties) in inFlight.Properties)
        {
            if (!inventory.Instances.TryGetValue(instanceId, out var info))
                continue; // removed in the same batch
            inventory.Instances[instanceId] = info with { Properties = properties };
            if (_equippedIndex < 0)
                continue;
            var equipped = inFlight.Snapshot[_equippedIndex];
            for (int slot = 0; slot < equipped.Length; slot++)
            {
                if (equipped[slot].IsInstance && equipped[slot].InstanceId == instanceId && !_changedSlots.Contains((byte)slot))
                    _changedSlots.Add((byte)slot);
            }
        }
    }
}
