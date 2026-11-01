using System.Globalization;
using GameServer.Events;
using GameServer.Items;
using GameServer.Jobs;
using GameServer.Persistence;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Crafting;
using Shared.Simulation.Items;
using Shared.Simulation.Jobs;
using Shared.Util;

namespace GameServer.Crafting;

/// <summary>
/// One gem request as the world reads it in phase 1: the target instance at <see cref="Target"/>, the gem's Normal-bag slot
/// (socket) or the gem's index in the item (remove); <see cref="InReach"/>: the bench gate passed; <see cref="Blocked"/>: in
/// combat or during an action.
/// </summary>
public readonly record struct GemAsk(uint RequestId, GemOp Op, uint StationEntityId, bool InReach, bool Blocked, InventoryLocation Target,
    long InstanceId, int GemSlot, int GemIndex, int Connection);

/// <summary>An operation whose save committed: its audit row, the answer, the gem's job and the connection that asked.</summary>
public sealed record CommittedGemOp(CraftOpRecord Record, GemResult Answer, string JobId, int GemItemNetId, int Connection);

/// <summary>
/// Gem socketing and removal at the jeweler's bench (spec 5.3 R-5.3-05, M3 decision 11; WP-M3-9), on the world thread.
/// <para>
/// <b>Socket</b> (the Jeweler's service, <see cref="IJobQuery"/>): an equipment instance with a free gem slot in the Safe bag
/// equipment area or Equipped and a cut gem (<c>GemDef</c>) in the Normal bag. The gem is reserved, a seeded roll (logged,
/// rule 6) against the tier's <c>GemSocketOdds</c> plus the job tree's <c>socketOddsPercent</c> (read now, already capped)
/// decides, and one class 2 operation consumes the gem: on success it is written into the item's properties
/// (<see cref="InventoryService.TryChangeProperties"/>, shown only once committed; a stolen gem taints the item, R-4.4-03); on failure the
/// gem is gone and the item is untouched. <b>Remove</b>: the gem comes back intact to the Normal bag (stolen when the item
/// is tainted, user answer 2026-10-31) for the tier's removal cost, lowered by the tree's <c>removalCostPercent</c> and
/// rounded by chance from the same seed.
/// </para>
/// <para>
/// Each operation asks for a save at once and is answered, with <c>GemSocketed</c>/<c>GemRemoved</c> on the bus, only after
/// its commit (rule 5, through <see cref="OpCommitted"/>); a crash before it loses the whole operation and shows nothing, so
/// a roll cannot be retried. One operation per character waits at a time (<c>GemBusy</c>); a repeated request id gets its
/// first answer. <see cref="InventoryService"/> stays the only item writer (rule 4). Equipment gems only: the Star Gems of
/// 14.1 share no code with this (R-14.1-04).
/// </para>
/// </summary>
public sealed class GemService : ICraftBatchSource
{
    /// <summary>The <c>Pcg32Rng</c> stream the operation seeds come from (the world seed is logged by the host).</summary>
    public const ulong SeedStream = 0x47534545UL; // "GSEE"

    /// <summary>Answers remembered per character, so a repeated request gets its first answer.</summary>
    private const int SeenAnswers = 16;

    private readonly GemRules _rules;
    private readonly InventoryService _inventory;
    private readonly IJobQuery? _jobs;
    private readonly IJobBonusQuery _bonuses;
    private readonly Func<long> _nowMs;
    private readonly Pcg32Rng _seeds;
    private readonly Dictionary<long, CharacterGems> _characters = new();
    private readonly List<ReserveItem> _reserve = new();

    public GemService(GemRules rules, InventoryService inventory, IJobQuery? jobs, IJobBonusQuery? bonuses, Func<long> nowMs, ulong worldSeed)
    {
        _rules = rules;
        _inventory = inventory;
        _jobs = jobs;
        _bonuses = bonuses ?? NoJobBonuses.Instance;
        _nowMs = nowMs;
        _seeds = new Pcg32Rng(worldSeed, SeedStream);
    }

    public GemRules Rules => _rules;

    /// <summary>Marks a character class 2 dirty with its save scheduler (set by the SessionManager).</summary>
    public Action<long>? MarkClass2 { get; set; }

    /// <summary>Asks for a save at the next tick (every gem operation, rule 5; set by the SessionManager).</summary>
    public Action<long, SaveReason>? RequestSave { get; set; }

    /// <summary>A save committed an operation of this character (set by the world: the events and the answer).</summary>
    public Action<long, CommittedGemOp>? OpCommitted { get; set; }

    public bool IsAttached(long characterId) => _characters.ContainsKey(characterId);

    /// <summary>Starts holding a character (nothing is loaded: the gems live in the item properties).</summary>
    public void Attach(long characterId) => _characters[characterId] = new CharacterGems();

    public void Detach(long characterId) => _characters.Remove(characterId);

    /// <summary>Operations applied and waiting for their save.</summary>
    public int WaitingCount(long characterId) =>
        _characters.TryGetValue(characterId, out var c) ? c.Pending.Count + (c.InFlight?.Ops.Count ?? 0) : 0;

    /// <summary>
    /// One request (phase 1). Returns the answer to send now (a refusal, or the first answer of a repeated request), or null
    /// when the operation was applied and waits for its save (answered through <see cref="OpCommitted"/>).
    /// </summary>
    public GemResult? Handle(long characterId, in GemAsk ask)
    {
        if (!_characters.TryGetValue(characterId, out var c) || !_inventory.IsAttached(characterId))
            return Refusal(ask, GemReason.GemUnavailable);
        if (c.Seen(ask.Connection, ask.RequestId) is { } seen)
            return seen;
        if (c.Waiting(ask.Connection, ask.RequestId))
            return null; // the same request while its save runs changes nothing; the answer comes after the commit
        if (c.Pending.Count > 0 || c.InFlight != null)
            return Refusal(ask, GemReason.GemBusy);
        if (!ask.InReach)
            return Refusal(ask, GemReason.StationOutOfReach);
        if (ask.Blocked)
            return Refusal(ask, GemReason.InCombat);

        // The target: an equipment instance with gem slots, in the Safe bag equipment area or Equipped, the one the player saw.
        var items = _inventory.CurrentInventory(characterId);
        var stack = items.TryIndex(ask.Target.Container, out _) ? items.At(ask.Target) : default;
        var target = stack.IsInstance && stack.InstanceId == ask.InstanceId && HoldsGear(ask.Target.Container)
            ? _rules.Item(stack.ItemNetId) : null;
        // The newest properties (an enchant change not yet committed included): what this change builds on, never shown.
        if (target == null || !_inventory.TryGetLatestProperties(characterId, ask.InstanceId, out var properties))
            return Refusal(ask, GemReason.NotSocketable);
        if (_inventory.IsReserved(characterId, ask.Target))
            return Refusal(ask, GemReason.ItemReserved);
        return ask.Op == GemOp.Socket
            ? Socket(characterId, c, ask, target, stack, properties)
            : Remove(characterId, c, ask, target, stack, properties);
    }

    public CraftBatch? TakeBatch(long characterId, Guid saveId)
    {
        if (!_characters.TryGetValue(characterId, out var c) || c.Pending.Count == 0)
            return null;
        if (c.InFlight != null)
            throw new InvalidOperationException($"character {characterId} already has a gem batch in flight");
        c.InFlight = new InFlightBatch(saveId, c.Pending.ToList());
        c.Pending.Clear();
        return new CraftBatch(c.InFlight.Ops.Select(op => op.Record).ToList());
    }

    public void Committed(long characterId, Guid saveId)
    {
        if (!_characters.TryGetValue(characterId, out var c) || c.InFlight is not { } inFlight || inFlight.SaveId != saveId)
            return; // a save without a batch, or a stale completion: idempotent
        c.InFlight = null;
        foreach (var op in inFlight.Ops)
        {
            c.Remember(op.Connection, op.Answer);
            OpCommitted?.Invoke(characterId, new CommittedGemOp(op.Record, op.Answer, op.JobId, op.GemItemNetId, op.Connection));
        }
    }

    /// <summary>The reservation and operation key of a gem operation: one per character and seed.</summary>
    public static string OperationKey(long characterId, ulong seed) =>
        string.Create(CultureInfo.InvariantCulture, $"gem:{characterId}:{seed:x16}");

    private GemResult? Socket(long characterId, CharacterGems c, in GemAsk ask, ItemDef target, in ItemStack targetStack,
        ItemProperties properties)
    {
        byte normal = (byte)_inventory.Rules.NormalBag.Code;
        var gemAt = new InventoryLocation(normal, ask.GemSlot);
        var items = _inventory.CurrentInventory(characterId);
        var gemStack = ask.GemSlot >= 0 && items.TryIndex(normal, out int normalIndex) && ask.GemSlot < items.BagAt(normalIndex).SlotCount
            ? items.At(gemAt) : default;
        var gemItem = gemStack.IsEmpty ? null : _rules.Item(gemStack.ItemNetId);
        var gem = gemItem == null ? null : _rules.Gem(gemItem.Id);
        string jobId = gem == null ? string.Empty : _rules.JobFor(gem);
        int jobLevel = jobId.Length == 0 ? 0 : _jobs?.JobLevel(characterId, jobId) ?? 0;
        int free = gemStack.IsEmpty ? 0 : _inventory.Unreserved(characterId, gemAt);
        var reason = _rules.CheckSocket(target, properties.Gems.Count, gemItem, jobLevel, free);
        if (reason == GemReason.MaterialsMissing && !gemStack.IsEmpty && gemStack.Count > 0)
            reason = GemReason.ItemReserved; // the units are there, a reservation holds them
        if (reason != GemReason.None)
            return Refusal(ask, reason);

        var odds = _rules.Odds(gem!.Tier)!;
        // The job tree's bonus is read once, now (spec 5.1), already capped by JobBonusCap.
        int bonus = _bonuses.Get(characterId, jobId, JobBonusKind.SocketOddsPercent, JobActivity.GemSocket);
        NextSeed(out ulong seed);
        var rolls = GemMath.Draw(seed, GemMath.SocketChance(odds.SuccessPercent, bonus), 0, 0);
        string key = OperationKey(characterId, seed);
        _reserve.Clear();
        _reserve.Add(new ReserveItem(gemAt, 1));
        if (_inventory.Reserve(characterId, key, _reserve) != ReserveResult.Ok)
            return Refusal(ask, GemReason.ItemReserved);
        bool taint = properties.Tainted || _inventory.TaintsOutput(characterId, key);
        string source = $"gem:{gem.Id}";
        if (!_inventory.TryRemoveReserved(characterId, key, key + ":gem",
                rolls.Success ? InventoryReason.GemSocket : InventoryReason.GemSocketFailed, source))
            throw new InvalidOperationException($"gem operation {key} lost its reservation");
        // The gem joins the item's properties in a pending change, shown only once its save committed (rule 5).
        if (rolls.Success && !_inventory.TryChangeProperties(characterId, ask.Target, next =>
            {
                next.Gems.Add(gem.Id);
                next.Tainted |= taint;
            }, key + ":item", InventoryReason.GemSocket, source))
            throw new InvalidOperationException($"gem operation {key}: the target changed after the checks");
        var inputs = new[] { string.Create(CultureInfo.InvariantCulture, $"{gem.Id}:{gemStack.Rarity}:1") };
        var record = new CraftOpRecord(key, CraftOpKind.Socket, gem.Id, gem.Tier, inputs, target.Id, 0, ask.InstanceId, 0,
            targetStack.Rarity, rolls.Success, seed, characterId, _nowMs());
        var answer = new GemResult
        {
            RequestId = ask.RequestId, Reason = GemReason.None, Op = GemOp.Socket, Success = rolls.Success,
            GemItemNetId = (ushort)gemItem!.NetId, InstanceId = ask.InstanceId,
        };
        return Applied(characterId, c, record, answer, jobId, gemItem.NetId, ask.Connection);
    }

    private GemResult? Remove(long characterId, CharacterGems c, in GemAsk ask, ItemDef target, in ItemStack targetStack,
        ItemProperties properties)
    {
        var gem = ask.GemIndex >= 0 && ask.GemIndex < properties.Gems.Count ? _rules.Gem(properties.Gems[ask.GemIndex]) : null;
        var odds = gem == null ? null : _rules.Odds(gem.Tier);
        string jobId = gem == null ? string.Empty : _rules.JobFor(gem);
        int jobLevel = jobId.Length == 0 ? 0 : _jobs?.JobLevel(characterId, jobId) ?? 0;
        var costItem = odds == null ? null : _rules.Item(odds.RemovalCostItemId);
        // The job tree's bonus is read once, now (spec 5.1), already capped by JobBonusCap.
        int percent = gem == null ? 0 : _bonuses.Get(characterId, jobId, JobBonusKind.RemovalCostPercent, JobActivity.GemRemove);
        int freeCost = costItem == null ? 0 : FreeNormalUnits(characterId, costItem);
        var reason = _rules.CheckRemove(target, properties.Gems, ask.GemIndex, jobLevel, freeCost, percent);
        if (reason != GemReason.None)
            return Refusal(ask, reason);
        var gemItem = _rules.Item(gem!.Id)!;
        if (_inventory.SpaceFor(characterId, gemItem, 0) < 1)
            return Refusal(ask, GemReason.BagFull);

        NextSeed(out ulong seed);
        var rolls = GemMath.Draw(seed, 0, odds!.RemovalCostCount, percent);
        string key = OperationKey(characterId, seed);
        string source = $"gem:{gem.Id}";
        if (rolls.RemovalCost > 0)
        {
            CollectNormalUnits(characterId, costItem!, rolls.RemovalCost);
            if (_inventory.Reserve(characterId, key, _reserve) != ReserveResult.Ok)
                return Refusal(ask, GemReason.ItemReserved);
            if (!_inventory.TryRemoveReserved(characterId, key, key + ":cost", InventoryReason.GemRemove, source))
                throw new InvalidOperationException($"gem operation {key} lost its reservation");
        }
        int index = ask.GemIndex;
        if (!_inventory.TryChangeProperties(characterId, ask.Target, next => next.Gems.RemoveAt(index), key + ":item",
                InventoryReason.GemRemove, source))
            throw new InvalidOperationException($"gem operation {key}: the target changed after the checks");
        // The gem comes back intact; out of a tainted item it is stolen, so a removal launders nothing (user answer 2026-10-31).
        var added = _inventory.TryAdd(characterId, gemItem, 0, 1, key + ":gem", InventoryReason.GemRemove, source, null,
            stolen: properties.Tainted);
        if (added.Added != 1)
            throw new InvalidOperationException($"gem operation {key}: the gem did not fit after the room check");
        var inputs = rolls.RemovalCost > 0
            ? new[] { string.Create(CultureInfo.InvariantCulture, $"{costItem!.Id}:0:{rolls.RemovalCost}") }
            : Array.Empty<string>();
        var record = new CraftOpRecord(key, CraftOpKind.Remove, gem.Id, gem.Tier, inputs, target.Id, 1, ask.InstanceId, 0,
            targetStack.Rarity, true, seed, characterId, _nowMs());
        var answer = new GemResult
        {
            RequestId = ask.RequestId, Reason = GemReason.None, Op = GemOp.Remove, Success = true, GemItemNetId = (ushort)gemItem.NetId,
            InstanceId = ask.InstanceId, CostItemNetId = (ushort)costItem!.NetId, CostCount = (ushort)rolls.RemovalCost,
        };
        return Applied(characterId, c, record, answer, jobId, gemItem.NetId, ask.Connection);
    }

    private GemResult? Applied(long characterId, CharacterGems c, CraftOpRecord record, GemResult answer, string jobId, int gemNetId,
        int connection)
    {
        c.Pending.Add(new PendingOp(record, answer, jobId, gemNetId, connection));
        MarkClass2?.Invoke(characterId);
        RequestSave?.Invoke(characterId, SaveReason.Gem);
        return null;
    }

    /// <summary>The Safe bag equipment area (the receiver of equipment) or the Equipped container.</summary>
    private bool HoldsGear(byte container)
    {
        var safe = _inventory.Rules.ReceiverOf(ItemCategory.Equipment);
        var equipped = _inventory.Rules.Equipped;
        return (safe != null && safe.Code == container) || (equipped != null && equipped.Code == container);
    }

    /// <summary>Unreserved units of <paramref name="item"/> in the Normal bag, every stack and rarity.</summary>
    private int FreeNormalUnits(long characterId, ItemDef item)
    {
        byte normal = (byte)_inventory.Rules.NormalBag.Code;
        var bag = _inventory.Current(characterId);
        int free = 0;
        for (int slot = 0; slot < bag.SlotCount; slot++)
        {
            if (!bag[slot].IsEmpty && bag[slot].ItemNetId == item.NetId)
                free += _inventory.Unreserved(characterId, new InventoryLocation(normal, slot));
        }
        return free;
    }

    /// <summary>Fills <see cref="_reserve"/> with <paramref name="count"/> unreserved units of the item, in slot order (rule 6).</summary>
    private void CollectNormalUnits(long characterId, ItemDef item, int count)
    {
        _reserve.Clear();
        byte normal = (byte)_inventory.Rules.NormalBag.Code;
        var bag = _inventory.Current(characterId);
        for (int slot = 0; slot < bag.SlotCount && count > 0; slot++)
        {
            if (bag[slot].IsEmpty || bag[slot].ItemNetId != item.NetId)
                continue;
            var at = new InventoryLocation(normal, slot);
            int take = Math.Min(count, _inventory.Unreserved(characterId, at));
            if (take <= 0)
                continue;
            _reserve.Add(new ReserveItem(at, take));
            count -= take;
        }
    }

    private void NextSeed(out ulong seed) => seed = ((ulong)_seeds.NextUInt() << 32) | _seeds.NextUInt();

    private static GemResult Refusal(in GemAsk ask, GemReason reason) =>
        new() { RequestId = ask.RequestId, Op = ask.Op, Reason = reason, InstanceId = ask.InstanceId };

    private sealed record PendingOp(CraftOpRecord Record, GemResult Answer, string JobId, int GemItemNetId, int Connection);

    private sealed record InFlightBatch(Guid SaveId, List<PendingOp> Ops);

    private sealed class CharacterGems
    {
        private readonly Queue<(int Connection, GemResult Answer)> _seen = new();

        public List<PendingOp> Pending { get; } = new();
        public InFlightBatch? InFlight { get; set; }

        public bool Waiting(int connection, uint requestId)
        {
            foreach (var op in Pending)
            {
                if (op.Connection == connection && op.Answer.RequestId == requestId)
                    return true;
            }
            if (InFlight != null)
            {
                foreach (var op in InFlight.Ops)
                {
                    if (op.Connection == connection && op.Answer.RequestId == requestId)
                        return true;
                }
            }
            return false;
        }

        public GemResult? Seen(int connection, uint requestId)
        {
            foreach (var (seenConnection, answer) in _seen)
            {
                if (seenConnection == connection && answer.RequestId == requestId)
                    return answer;
            }
            return null;
        }

        public void Remember(int connection, GemResult answer)
        {
            if (_seen.Count == SeenAnswers)
                _seen.Dequeue();
            _seen.Enqueue((connection, answer));
        }
    }
}
