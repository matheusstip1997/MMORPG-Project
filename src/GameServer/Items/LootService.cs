using GameServer.Entities;
using GameServer.Events;
using GameServer.World;
using Shared.Protocol;
using Shared.Simulation;
using Shared.Simulation.Items;
using Shared.Util;

namespace GameServer.Items;

/// <summary>Where <see cref="LootService"/> puts and removes drops: the world's entity list and grid.</summary>
internal interface ILootWorld
{
    /// <summary>A walkable spot for a drop near <paramref name="wanted"/> (the death position when the spot is blocked).</summary>
    (MovementState Movement, int HeightCm) PlaceDrop(FixVec2 wanted, FixVec2 fallback);

    LootDrop AddDrop(Func<uint, LootDrop> create);

    void RemoveDrop(LootDrop drop);

    Entity? FindEntity(uint id);
}

/// <summary>
/// Monster loot and ground drops (R-4.3-01, spec 4.3 <c>LootService</c>, backlog G).
/// <list type="bullet">
/// <item>Consumes <see cref="Killed"/> (phase 11), idempotent per event ID (<see cref="EventWatermark"/>): a monster's
/// loot table is rolled with the world's seeded loot generator (rule 6; the host logs seed and stream) and each rolled
/// stack becomes a <see cref="LootDrop"/> where the monster died, locked to the killer for <c>partyLootLockMs</c>
/// (R-4.3-02; M1 has no parties) and gone after <c>bagExpiryMs</c>.</item>
/// <item>Pickups (phase 1, players in entity ID order): checked here (alive, can see it, in range, lock), then executed
/// by <see cref="InventoryService"/>, the only writer of items (rule 4). Take what fits; the rest stays on the ground
/// (user decision 2026-10-24). Two players asking for one drop in the same tick: the first in order takes it, the
/// second finds it gone, so a drop can never be taken twice.</item>
/// <item>Expiry (phase 8): drops past their time leave the world.</item>
/// </list>
/// TODO(M4): player death loot bags (<c>Died</c>, R-4.3-03), party rolls (R-4.3-02), mail overflow, pets (R-4.3-04).
/// </summary>
internal sealed class LootService : IEventConsumer<Killed>
{
    private readonly ItemRules _rules;
    private readonly InventoryService _inventory;
    private readonly ILootWorld _world;
    private readonly EventBus? _events;
    private readonly IRng _rng;
    private readonly IVisibilityQuery _visibility;
    private readonly string _runId;
    private readonly Func<long> _nowMs;
    private readonly List<LootDrop> _drops = new(); // by entity id
    private readonly List<ItemStack> _rolled = new();
    private readonly List<LootDrop> _taken = new();
    private readonly long _pickupRangeSq;
    private EventWatermark _watermark;

    /// <param name="runId">Unique per world run; with the kill's event id it makes every drop key unique.</param>
    /// <param name="nowMs">World simulation time (<c>GameWorld.NowMs</c>).</param>
    public LootService(ItemRules rules, InventoryService inventory, ILootWorld world, EventBus? events, IRng rng,
        IVisibilityQuery visibility, string runId, Func<long> nowMs)
    {
        _rules = rules;
        _inventory = inventory;
        _world = world;
        _events = events;
        _rng = rng;
        _visibility = visibility;
        _runId = runId;
        _nowMs = nowMs;
        long range = rules.Settings.PickupRangeCm;
        _pickupRangeSq = range * range;
    }

    /// <summary>Drops on the ground, in entity ID order.</summary>
    public IReadOnlyList<LootDrop> Drops => _drops;

    /// <summary>Rolled stacks not spawned because the world held <c>LootSettings.maxDropsPerWorld</c> drops.</summary>
    public long DropsOverCap { get; private set; }

    public void Subscribe(EventBus events) => events.Subscribe<Killed>(this);

    public void Handle(in EventEnvelope envelope, in Killed payload)
    {
        if (!_watermark.TryAccept(envelope))
            return; // a re-dispatched kill drops nothing twice
        if (payload.VictimCharacterId != 0 || !_rules.TryGetLoot(payload.VictimDefId, out var loot))
            return; // players drop no loot table (TODO(M4): death loot bags); monsters without a table drop nothing

        _rolled.Clear();
        LootRoll.Roll(loot.Choices, loot.Rolls, loot.NothingWeight, _rng, _rolled);
        if (_rolled.Count == 0)
            return;

        long now = _nowMs();
        var death = FixVec2.FromCm(payload.XCm, payload.YCm);
        for (int i = 0; i < _rolled.Count; i++)
        {
            if (_drops.Count >= _rules.Settings.MaxDropsPerWorld)
            {
                DropsOverCap += _rolled.Count - i;
                return;
            }
            var stack = _rolled[i];
            if (!_rules.TryGetItem(stack.ItemNetId, out var item))
                continue;
            var (movement, height) = _world.PlaceDrop(Scatter(death, i, _rolled.Count), death);
            string key = $"{_runId}:{envelope.EventId}:{i}";
            long owner = envelope.ActorCharacterId;
            var drop = _world.AddDrop(id => new LootDrop(id, item, stack.Rarity, stack.Count, movement, height, key,
                owner, now + _rules.LootLockMs, now + _rules.DropExpiryMs));
            _drops.Add(drop);
            _events?.Publish(new LootDropped(drop.Id, envelope.EventId, payload.VictimDefId, stack.ItemNetId, stack.Rarity,
                stack.Count, envelope.ActorCharacterId), envelope.ActorCharacterId, envelope.RegionId);
        }
    }

    /// <summary>
    /// Phase 1: one pickup request of <paramref name="player"/>. The answer goes to <see cref="Player"/>'s pending pickup
    /// results (sent in phase 10).
    /// </summary>
    public PickupReason Pickup(Player player, uint dropId, out int taken)
    {
        taken = 0;
        if (player.Combatant is { IsAlive: false })
            return PickupReason.PickupWhileDead;
        if (_world.FindEntity(dropId) is not LootDrop { Count: > 0 } drop || !_visibility.CanSee(player, drop)
            || !_inventory.IsAttached(player.CharacterId))
            return PickupReason.LootNotFound;
        long dx = drop.XCm - player.XCm, dy = drop.YCm - player.YCm;
        if (dx * dx + dy * dy > _pickupRangeSq)
            return PickupReason.OutOfRange;
        if (drop.IsLockedFor(player.CharacterId, _nowMs()))
            return PickupReason.LootLocked;

        string opKey = $"{drop.DropKey}:p{drop.Pickups}";
        var result = _inventory.TryAdd(player.CharacterId, drop.Item, drop.Rarity, drop.Count, opKey,
            InventoryReason.LootPickup, drop.DropKey);
        if (result.Added == 0)
            return PickupReason.BagFull;

        taken = result.Added;
        drop.Pickups++;
        drop.Count -= result.Added;
        _events?.Publish(new LootPickedUp(drop.Id, player.CharacterId, drop.DefId, result.Added, drop.Count),
            player.CharacterId, player.RegionIndex);
        if (drop.Count == 0)
            _taken.Add(drop); // leaves the world in RemoveTaken, after the phase 1 loop over the entities
        return PickupReason.None;
    }

    /// <summary>Phase 8: drops nobody took within <c>bagExpiryMs</c> leave the world.</summary>
    public void Expire()
    {
        long now = _nowMs();
        for (int i = 0; i < _drops.Count; i++)
        {
            var drop = _drops[i];
            if (now < drop.ExpiresAtMs)
                continue;
            _events?.Publish(new LootDropExpired(drop.Id, drop.DefId, drop.Count));
            _world.RemoveDrop(drop);
            _drops.RemoveAt(i);
            i--;
        }
    }

    /// <summary>Phase 1, after every pickup: drops taken whole leave the world (observers get a despawn).</summary>
    public void RemoveTaken()
    {
        foreach (var drop in _taken)
        {
            _world.RemoveDrop(drop);
            _drops.Remove(drop);
        }
        _taken.Clear();
    }

    /// <summary>Drop <paramref name="index"/> of <paramref name="count"/> on a ring around the death position (fixed angles, rule 6).</summary>
    private FixVec2 Scatter(FixVec2 center, int index, int count)
    {
        if (count == 1 || _rules.Settings.DropScatterCm == 0)
            return center;
        var angle = (ushort)(index * 65536 / count);
        return center + FixedTrig.Along(angle, FixedPoint.FromCm(_rules.Settings.DropScatterCm));
    }
}
