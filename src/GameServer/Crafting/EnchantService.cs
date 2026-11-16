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
/// What one enchant request asks for, as the world reads it in phase 1 (<see cref="InReach"/>: the station gate passed).
/// <see cref="ContractId"/> (WP-M4-10 part C): an enchant contract's attempt on the customer's escrowed item; then
/// <see cref="At"/> and <see cref="InstanceId"/> are not read. 0 for an own item.
/// </summary>
public readonly record struct EnchantAsk(uint RequestId, uint StationEntityId, NpcInteraction Station, InventoryLocation At, long InstanceId,
    bool InReach, bool Blocked, int Connection, long ContractId = 0);

/// <summary>An attempt whose save committed: its audit row, the answer, the item and its connection.</summary>
public sealed record CommittedEnchantOp(CraftOpRecord Record, EnchantResult Answer, string JobId, int ItemNetId, int Stones, int Connection);

/// <summary>
/// Enchanting (spec 5.3 R-5.3-04, M3 decision 10; WP-M3-8) for every character held by this world, on the world thread.
/// <para>
/// An attempt is one request at the station of the item's job, out of combat: an equipment instance of the character's job
/// in the Safe bag's equipment area or Equipped, below +9, with the stones of its tier (<c>EnchantMaterial</c>) free in the
/// Normal bag. The stones and, under its own key, the item are reserved (<see cref="InventoryService.Reserve"/>: the item
/// cannot be moved, equipped, sold or enchanted again until the commit); the job tree's <c>enchantOddsPercent</c> is read
/// once (<see cref="IJobBonusQuery"/>, capped by <c>JobBonusCap</c>); one draw from a logged per-attempt seed
/// (<see cref="EnchantMath"/>) gives +1, no change or -1 within +0..+9. In one class 2 operation the stones are consumed,
/// always (a sink, R-4.2-04; <see cref="InventoryService.TryRemoveReserved"/>), and the level is written into the item's
/// properties (<see cref="InventoryService.TryChangeProperties"/>; the item is never destroyed). The save is asked for at
/// once; the answer, <see cref="Enchanted"/> and the stats (<see cref="EquipmentChanged"/>) come only after the commit
/// (rule 5), so a crash before it leaves the stones and the level untouched and shows nothing.
/// </para>
/// <para>
/// An <b>enchant contract</b>'s attempt (WP-M4-10 part C, spec 5.4; user answers 2026-11-16) works on the customer's item in the
/// contract's escrow (<see cref="IEnchantContracts"/>): the same gates, checks, seed and odds, with the crafter's job and job-tree
/// bonus; the stones are the crafter's own (reserved from its Normal bag, never stolen) or the customer's escrowed ones, as the
/// terms say. One attempt settles the contract in the crafter's one save, whatever the outcome: the crafter's stones consumed, the
/// escrow settled (the gold to the crafter less the fee, the customer's stones consumed) and the item mailed back to the customer
/// as the same instance at its new level. The answer and <see cref="Enchanted"/> come after the commit as for an own attempt.
/// </para>
/// <see cref="InventoryService"/> stays the only item writer (rule 4).
/// </summary>
public sealed class EnchantService : ICraftBatchSource
{
    /// <summary>The <c>Pcg32Rng</c> stream the attempt seeds come from (the world seed is logged by the host).</summary>
    public const ulong SeedStream = 0x45534545UL; // "ESEE"

    /// <summary>Answers remembered per character, so a repeated request gets its first answer.</summary>
    private const int SeenAnswers = 16;

    private readonly EnchantRules _rules;
    private readonly InventoryService _inventory;
    private readonly IJobQuery? _jobs;
    private readonly IJobBonusQuery _bonuses;
    private readonly Func<long> _nowMs;
    private readonly Pcg32Rng _seeds;
    private readonly Dictionary<long, CharacterEnchant> _characters = new();
    private readonly List<ReserveItem> _reserve = new();

    public EnchantService(EnchantRules rules, InventoryService inventory, IJobQuery? jobs, IJobBonusQuery? bonuses, Func<long> nowMs,
        ulong worldSeed)
    {
        _rules = rules;
        _inventory = inventory;
        _jobs = jobs;
        _bonuses = bonuses ?? NoJobBonuses.Instance;
        _nowMs = nowMs;
        _seeds = new Pcg32Rng(worldSeed, SeedStream);
    }

    public EnchantRules Rules => _rules;

    /// <summary>Marks a character class 2 dirty with its save scheduler (set by the SessionManager).</summary>
    public Action<long>? MarkClass2 { get; set; }

    /// <summary>Asks for a save at the next tick (every attempt, R-7.1-05; set by the SessionManager).</summary>
    public Action<long, SaveReason>? RequestSave { get; set; }

    /// <summary>A save committed an attempt of this character (set by the world: the events and the answer).</summary>
    public Action<long, CommittedEnchantOp>? OpCommitted { get; set; }

    /// <summary>The enchant contracts (WP-M4-10 part C, <c>ContractService</c>; set by the world); null: a contract attempt is refused.</summary>
    public IEnchantContracts? Contracts { get; set; }

    public bool IsAttached(long characterId) => _characters.ContainsKey(characterId);

    public void Attach(long characterId) => _characters[characterId] = new CharacterEnchant();

    /// <summary>The character left the world (its reservations end with the inventory's detach).</summary>
    public void Detach(long characterId) => _characters.Remove(characterId);

    /// <summary>Attempts applied and waiting for their save.</summary>
    public int WaitingCount(long characterId) =>
        _characters.TryGetValue(characterId, out var c) ? c.Pending.Count + (c.InFlight?.Ops.Count ?? 0) : 0;

    /// <summary>
    /// One attempt (phase 1). Returns the answer to send now (a refusal, or the first answer of a repeated request), or null
    /// when the attempt was applied and waits for its save (answered through <see cref="OpCommitted"/>) or the same request
    /// is waiting already.
    /// </summary>
    public EnchantResult? Request(long characterId, in EnchantAsk ask)
    {
        if (!_characters.TryGetValue(characterId, out var c) || !_inventory.IsAttached(characterId))
            return Refusal(ask, EnchantReason.EnchantUnavailable);
        if (c.Seen(ask.Connection, ask.RequestId) is { } seen)
            return seen;
        if (c.IsWaiting(ask.Connection, ask.RequestId))
            return null; // a double submit while it saves changes nothing
        if (!ask.InReach)
            return Refusal(ask, EnchantReason.StationOutOfReach);
        if (ask.Blocked)
            return Refusal(ask, EnchantReason.InCombat);
        if (ask.ContractId != 0)
            return RequestContract(characterId, c, ask);

        var inventory = _inventory.CurrentInventory(characterId);
        if (!_rules.IsTargetContainer(ask.At.Container) || !inventory.TryIndex(ask.At.Container, out int index) || ask.At.Slot < 0
            || ask.At.Slot >= inventory.BagAt(index).SlotCount)
            return Refusal(ask, EnchantReason.NotEnchantable);
        var stack = inventory.BagAt(index)[ask.At.Slot];
        if (!stack.IsInstance || stack.InstanceId != ask.InstanceId
            || !_inventory.TryGetLatestProperties(characterId, stack.InstanceId, out var properties))
            return Refusal(ask, EnchantReason.NotEnchantable);
        var item = _rules.Item(stack.ItemNetId);
        int from = properties.Enchant;
        var reason = _rules.Check(item, ask.Station, job => _jobs?.HasJob(characterId, job) == true, from);
        if (reason != EnchantReason.None)
            return Refusal(ask, reason);
        if (_inventory.Unreserved(characterId, ask.At) < 1)
            return Refusal(ask, EnchantReason.ItemReserved);
        if (!_rules.TryMaterial(item!, out var stone, out int count))
            return Refusal(ask, EnchantReason.EnchantUnavailable);
        reason = PickStones(characterId, stone, count);
        if (reason != EnchantReason.None)
            return Refusal(ask, reason);

        ulong seed = NextSeed();
        string key = OpKey(characterId, seed);
        var reserved = _inventory.Reserve(characterId, key, _reserve);
        if (reserved != ReserveResult.Ok)
            return Refusal(ask, reserved == ReserveResult.ItemReserved ? EnchantReason.ItemReserved : EnchantReason.MaterialsMissing);
        if (_inventory.Reserve(characterId, ItemKey(key), new[] { new ReserveItem(ask.At, 1) }) != ReserveResult.Ok)
        {
            _inventory.Release(characterId, key);
            return Refusal(ask, EnchantReason.ItemReserved);
        }

        string jobId = _rules.JobOf(item!);
        // The job tree's bonus, read once when the attempt starts (spec 5.1; capped by JobBonusCap in the query).
        int bonus = Math.Max(0, _bonuses.Get(characterId, jobId, JobBonusKind.EnchantOddsPercent, JobActivity.Enchant));
        var weights = EnchantMath.Weights(_rules.Odds(from)!, bonus);
        var outcome = EnchantMath.Outcome(weights, EnchantMath.Draw(seed));
        int to = EnchantMath.Apply(from, outcome);

        var inputs = StoneInputs(characterId, key, stone);
        string source = string.Create(CultureInfo.InvariantCulture, $"enchant:{item!.Id}:{from}->{to}");
        // A stolen stone does not taint the item (R-5.3-04, decided 2026-11-01): the stone is destroyed and the item
        // keeps its own taint; R-4.4-03 taints crafted outputs and socketed gems only.
        if (!_inventory.TryRemoveReserved(characterId, key, key + ":in", InventoryReason.Enchant, source))
            throw new InvalidOperationException($"enchant {key} lost its stone reservation");
        if (!_inventory.TryChangeProperties(characterId, ask.At, p => p.Enchant = to, key + ":lvl", InventoryReason.Enchant, source))
            throw new InvalidOperationException($"enchant {key} could not write the level of instance {stack.InstanceId}");

        int tier = _rules.TierOf(item);
        var record = new CraftOpRecord(key, CraftOpKind.Enchant, item.Id, tier, inputs, item.Id, 1, stack.InstanceId, 0, stack.Rarity,
            true, seed, characterId, _nowMs(), false, from, to);
        var answer = new EnchantResult
        {
            RequestId = ask.RequestId, Reason = EnchantReason.None, InstanceId = stack.InstanceId, ItemNetId = stack.ItemNetId,
            FromLevel = (byte)from, ToLevel = (byte)to, StonesUsed = (byte)count,
        };
        c.Pending.Add(new PendingOp(record, answer, jobId, item.NetId, count, ask.Connection));
        MarkClass2?.Invoke(characterId);
        RequestSave?.Invoke(characterId, SaveReason.Enchant);
        return null;
    }

    /// <summary>
    /// An enchant contract's attempt (WP-M4-10 part C): the item from the contract's escrow, the gates and checks of an own attempt
    /// with the crafter's job, the stones of the terms' supplier, the same seeded draw; the settlement is checked before anything
    /// moves (a refusal moves nothing), then the crafter's stones are consumed and the contract settled, all in one class 2 op.
    /// </summary>
    private EnchantResult? RequestContract(long characterId, CharacterEnchant c, in EnchantAsk ask)
    {
        if (Contracts == null)
            return Refusal(ask, EnchantReason.ContractNotFound);
        var reason = Contracts.OpenEnchant(characterId, ask.ContractId, out var terms);
        if (reason != EnchantReason.None)
            return Refusal(ask, reason);
        var held = terms!.Item;
        var item = _rules.Item(held.DefId);
        int from = terms.Level;
        reason = _rules.Check(item, ask.Station, job => _jobs?.HasJob(characterId, job) == true, from);
        if (reason != EnchantReason.None)
            return Refusal(ask, reason, held.InstanceId);
        if (!_rules.TryMaterial(item!, out var stone, out int count))
            return Refusal(ask, EnchantReason.EnchantUnavailable, held.InstanceId);
        // The terms named the tier's stones at the proposal; tables changed since leave the contract unworkable (never a different cost).
        if (terms.StoneNetId != stone.NetId || terms.StoneCount != count)
            return Refusal(ask, EnchantReason.ContractStateInvalid, held.InstanceId);
        bool crafterStones = terms.StonesSupplier == ContractSupplier.Crafter;
        _reserve.Clear();
        if (crafterStones)
        {
            // The crafter's own stones, never stolen (R-5.4-03, R-4.1-04): a stolen stack is not counted.
            reason = PickStones(characterId, stone, count, tradableOnly: true);
            if (reason != EnchantReason.None)
                return Refusal(ask, reason, held.InstanceId);
        }
        else if (CustomerStoneUnits(terms.CustomerStones, stone) != count)
            return Refusal(ask, EnchantReason.ContractStateInvalid, held.InstanceId);

        ulong seed = NextSeed();
        string key = OpKey(characterId, seed);
        string jobId = _rules.JobOf(item!);
        // The crafter's job tree bonus, read once (spec 5.1): the attempt is the crafter's work.
        int bonus = Math.Max(0, _bonuses.Get(characterId, jobId, JobBonusKind.EnchantOddsPercent, JobActivity.Enchant));
        var weights = EnchantMath.Weights(_rules.Odds(from)!, bonus);
        var outcome = EnchantMath.Outcome(weights, EnchantMath.Draw(seed));
        int to = EnchantMath.Apply(from, outcome);
        var output = new EnchantContractOutput(key, from, to);
        reason = Contracts.PrepareSettle(characterId, ask.ContractId, output);
        if (reason != EnchantReason.None)
            return Refusal(ask, reason, held.InstanceId);

        string source = string.Create(CultureInfo.InvariantCulture, $"enchant:{item!.Id}:{from}->{to}:contract:{ask.ContractId}");
        List<string> inputs;
        if (crafterStones)
        {
            var reserved = _inventory.Reserve(characterId, key, _reserve);
            if (reserved != ReserveResult.Ok)
                return Refusal(ask, reserved == ReserveResult.ItemReserved ? EnchantReason.ItemReserved : EnchantReason.MaterialsMissing,
                    held.InstanceId);
            inputs = StoneInputs(characterId, key, stone);
            if (!_inventory.TryRemoveReserved(characterId, key, key + ":in", InventoryReason.Enchant, source))
                throw new InvalidOperationException($"enchant {key} lost its stone reservation");
        }
        else
        {
            inputs = new List<string>(terms.CustomerStones.Count);
            foreach (var stones in terms.CustomerStones)
                inputs.Add(string.Create(CultureInfo.InvariantCulture, $"{stones.DefId}:{stones.Rarity}:{stones.Count}"));
        }
        Contracts.Settle(characterId, ask.ContractId, output);

        var record = new CraftOpRecord(key, CraftOpKind.Enchant, item.Id, _rules.TierOf(item), inputs, item.Id, 1, held.InstanceId, 0,
            held.Rarity, true, seed, terms.CustomerId, _nowMs(), false, from, to, ContractId: ask.ContractId);
        var answer = new EnchantResult
        {
            RequestId = ask.RequestId, Reason = EnchantReason.None, InstanceId = held.InstanceId, ItemNetId = (ushort)item.NetId,
            FromLevel = (byte)from, ToLevel = (byte)to, StonesUsed = (byte)count,
        };
        c.Pending.Add(new PendingOp(record, answer, jobId, item.NetId, count, ask.Connection));
        MarkClass2?.Invoke(characterId);
        RequestSave?.Invoke(characterId, SaveReason.Enchant);
        return null;
    }

    /// <summary>The units of <paramref name="stone"/> among a contract escrow's stone rows.</summary>
    private static int CustomerStoneUnits(IReadOnlyList<EscrowItemRow> rows, ItemDef stone)
    {
        int units = 0;
        foreach (var row in rows)
        {
            if (row.DefId == stone.Id)
                units += row.Count;
        }
        return units;
    }

    public CraftBatch? TakeBatch(long characterId, Guid saveId)
    {
        if (!_characters.TryGetValue(characterId, out var c) || c.Pending.Count == 0)
            return null;
        if (c.InFlight != null)
            throw new InvalidOperationException($"character {characterId} already has an enchant batch in flight");
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
            _inventory.Release(characterId, ItemKey(op.Record.OpKey));
            OpCommitted?.Invoke(characterId, new CommittedEnchantOp(op.Record, op.Answer, op.JobId, op.ItemNetId, op.Stones, op.Connection));
        }
    }

    /// <summary>The operation key of an attempt (and its stone reservation): one per character and seed.</summary>
    public static string OpKey(long characterId, ulong seed) =>
        string.Create(CultureInfo.InvariantCulture, $"enchant:{characterId}:{seed:x16}");

    /// <summary>The key the item is reserved under until its attempt committed.</summary>
    public static string ItemKey(string opKey) => opKey + ":item";

    /// <summary>
    /// Fills <see cref="_reserve"/> with <paramref name="count"/> free units of <paramref name="stone"/>, Normal bag in slot order;
    /// <paramref name="tradableOnly"/> (a contract's stones) leaves out stolen stacks.
    /// </summary>
    private EnchantReason PickStones(long characterId, ItemDef stone, int count, bool tradableOnly = false)
    {
        _reserve.Clear();
        var inventory = _inventory.CurrentInventory(characterId);
        byte normal = (byte)_inventory.Rules.NormalBag.Code;
        if (!inventory.TryIndex(normal, out int index))
            return EnchantReason.MaterialsMissing;
        var bag = inventory.BagAt(index);
        int left = count;
        for (int slot = 0; slot < bag.SlotCount && left > 0; slot++)
        {
            if (bag[slot].IsEmpty || bag[slot].ItemNetId != stone.NetId || (tradableOnly && (bag[slot].Stolen || bag[slot].StolenFrom != 0)))
                continue;
            var at = new InventoryLocation(normal, slot);
            int take = Math.Min(left, _inventory.Unreserved(characterId, at));
            if (take <= 0)
                continue;
            _reserve.Add(new ReserveItem(at, take));
            left -= take;
        }
        return left > 0 ? EnchantReason.MaterialsMissing : EnchantReason.None;
    }

    /// <summary>The reserved stones as audit inputs, <c>itemId:rarity:count</c> per stack (slot order).</summary>
    private List<string> StoneInputs(long characterId, string key, ItemDef stone)
    {
        var inputs = new List<string>(_reserve.Count);
        var inventory = _inventory.CurrentInventory(characterId);
        foreach (var reserve in _reserve)
            inputs.Add(string.Create(CultureInfo.InvariantCulture,
                $"{stone.Id}:{inventory.At(reserve.At).Rarity}:{reserve.Count}"));
        return inputs;
    }

    private ulong NextSeed() => ((ulong)_seeds.NextUInt() << 32) | _seeds.NextUInt();

    private static EnchantResult Refusal(in EnchantAsk ask, EnchantReason reason) =>
        new() { RequestId = ask.RequestId, Reason = reason, InstanceId = ask.InstanceId };

    private static EnchantResult Refusal(in EnchantAsk ask, EnchantReason reason, long instanceId) =>
        new() { RequestId = ask.RequestId, Reason = reason, InstanceId = instanceId };

    private sealed record PendingOp(CraftOpRecord Record, EnchantResult Answer, string JobId, int ItemNetId, int Stones, int Connection);

    private sealed record InFlightBatch(Guid SaveId, List<PendingOp> Ops);

    private sealed class CharacterEnchant
    {
        private readonly Queue<(int Connection, EnchantResult Answer)> _seen = new();

        public List<PendingOp> Pending { get; } = new();
        public InFlightBatch? InFlight { get; set; }

        public bool IsWaiting(int connection, uint requestId)
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

        public EnchantResult? Seen(int connection, uint requestId)
        {
            foreach (var (seenConnection, answer) in _seen)
            {
                if (seenConnection == connection && answer.RequestId == requestId)
                    return answer;
            }
            return null;
        }

        public void Remember(int connection, EnchantResult answer)
        {
            if (_seen.Count == SeenAnswers)
                _seen.Dequeue();
            _seen.Enqueue((connection, answer));
        }
    }
}
