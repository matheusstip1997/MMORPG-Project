using System.Globalization;
using GameServer.Entities;
using GameServer.Events;
using GameServer.Items.Mail;
using GameServer.Persistence;
using GameServer.Progression;
using GameServer.Social;
using GameServer.World;
using Shared.Data.Tables;
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
/// Monster loot, ground drops and party loot rolls (R-4.3-01, R-4.3-02; spec 4.3 <c>LootService</c> and "M4 party loot";
/// backlog G, WP-M4-6).
/// <list type="bullet">
/// <item>Consumes <see cref="Killed"/> (phase 11), idempotent per event ID (<see cref="EventWatermark"/>): a monster's
/// loot table is rolled with the world's seeded loot generator (rule 6; the host logs seed and stream) and each rolled
/// stack becomes a <see cref="LootDrop"/> where the monster died, locked for <c>PartyLootParams.lockMs</c> and gone after
/// <c>bagExpiryMs</c>. A killer in a party locks the drop to the party (<see cref="LootDrop.OwnerPartyId"/>), and the
/// members eligible for its rolls are fixed then (<see cref="LootDrop.EligibleAtKill"/>: a hit on the monster through
/// <see cref="IKillContributions"/>, read before its ledger is dropped, or within <c>killRangeCm</c> of the kill).</item>
/// <item>Pickups (phase 1, players in entity ID order): checked here (alive, can see it, in range, lock), then executed
/// by <see cref="InventoryService"/>, the only writer of items (rule 4). Two players asking for one drop in the same tick:
/// the first in order takes it, the second finds it gone (or smaller), so a drop can never be taken twice.
/// <list type="bullet">
/// <item>A pickup by a member of the drop's party <b>rolls</b> (<see cref="Roll"/>), inside the lock and after it (user
/// answer 2026-11-13): among the eligible members still in the party, connected and within the interest square of the drop
/// (the picker alone when none is), unit by unit with <see cref="PartyLoot"/> weighted by the loot debts
/// (<see cref="IPartyLootDebts"/>). Each winner's bag takes what fits (class 2, reason <c>LootPickup</c>); the rest goes to
/// the winner by mail (<see cref="MailService.Send"/>, kind <c>LootOverflow</c>, on the winner's class 1 save). Each winner's
/// part is announced to the party (<see cref="LootRollNotice"/>) once the winner's save committed it.</item>
/// <item>Any other pickup takes what fits and leaves the rest on the ground (user decision 2026-10-24); inside the lock it
/// is refused to everyone but the party's members (the killer alone when it had no party, or once its party dissolved).</item>
/// </list></item>
/// <item>Expiry (phase 8): drops past their time leave the world.</item>
/// </list>
/// TODO(M4): player death loot bags (<c>Died</c>, R-4.3-03, WP-M4-9); pets (R-4.3-04, M4 decision 9: later).
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
    private readonly Dictionary<string, PendingNotice> _notices = new(StringComparer.Ordinal);
    private readonly List<string> _staleNotices = new();
    private readonly long[] _members = new long[PartyLoot.MaxCandidates];
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

    /// <summary>Roll parts waiting for their winner's save (tests).</summary>
    public int PendingNotices => _notices.Count;

    // ---- party loot (WP-M4-6; set by the world when it has parties) ------------------------------------------------

    /// <summary>The party loot rules; null: no party rolls, every drop is locked to its killer alone.</summary>
    public PartyLootParams? PartyLootRules { get; set; }

    public IPartyQuery Parties { get; set; } = NoParties.Instance;

    /// <summary>The loot debts (<see cref="PartyService"/>); null: every debt is 0.</summary>
    public IPartyLootDebts? Debts { get; set; }

    /// <summary>The hits on a victim, read at its kill; null: only the range at the kill makes a member eligible.</summary>
    public IKillContributions? Contributions { get; set; }

    /// <summary>The world's player of a character (online or lingering); null when it is not in the world.</summary>
    public Func<long, Player?> PlayerOf { get; set; } = _ => null;

    /// <summary>The half-size of the interest square a candidate must have the drop in (<c>StartingValuesM1.interestRadiusCm</c>).</summary>
    public int InterestRadiusCm { get; set; }

    /// <summary>An item's value in gold per unit for the loot debt (<c>PriceBase</c>), 0 without one.</summary>
    public Func<ItemDef, long> UnitValue { get; set; } = _ => 0;

    /// <summary>Where a winning that does not fit goes (WP-M4-3); null: it stays on the ground.</summary>
    public MailService? Mail { get; set; }

    public void Subscribe(EventBus events) => events.Subscribe<Killed>(this);

    public void Handle(in EventEnvelope envelope, in Killed payload)
    {
        if (!_watermark.TryAccept(envelope))
            return; // a re-dispatched kill drops nothing twice
        if (payload.VictimCharacterId != 0 || !_rules.TryGetLoot(payload.VictimDefId, out var loot))
            return; // players drop no loot table (TODO(M4): death loot bags, WP-M4-9); monsters without a table drop nothing

        _rolled.Clear();
        LootRoll.Roll(loot.Choices, loot.Rolls, loot.NothingWeight, _rng, _rolled);
        if (_rolled.Count == 0)
            return;

        long now = _nowMs();
        long owner = envelope.ActorCharacterId;
        long partyId = owner != 0 && PartyLootRules != null ? Parties.PartyIdOf(owner) : 0;
        long[]? eligible = partyId != 0 ? EligibleAtKill(partyId, owner, payload) : null;
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
            var drop = _world.AddDrop(id => new LootDrop(id, item, stack.Rarity, stack.Count, movement, height, key,
                owner, now + _rules.LootLockMs, now + _rules.DropExpiryMs, partyId, eligible));
            _drops.Add(drop);
            _events?.Publish(new LootDropped(drop.Id, envelope.EventId, payload.VictimDefId, stack.ItemNetId, stack.Rarity,
                stack.Count, envelope.ActorCharacterId), envelope.ActorCharacterId, envelope.RegionId);
        }
    }

    /// <summary>
    /// The killer's party members eligible for the kill's rolls, in join order (M4 decision 2): the killer, every member that hit
    /// the victim, and every member in the world within <c>killRangeCm</c> of the kill.
    /// </summary>
    private long[] EligibleAtKill(long partyId, long killer, in Killed payload)
    {
        int count = Parties.MembersOf(partyId, _members);
        long range = PartyLootRules!.KillRangeCm;
        int eligible = 0;
        for (int i = 0; i < count; i++)
        {
            long member = _members[i];
            bool yes = member == killer || (Contributions?.HasHit(payload.VictimId, member) ?? false);
            if (!yes && PlayerOf(member) is { } player)
            {
                long dx = player.XCm - payload.XCm, dy = player.YCm - payload.YCm;
                yes = dx * dx + dy * dy <= range * range;
            }
            if (yes)
                _members[eligible++] = member;
        }
        return _members.AsSpan(0, eligible).ToArray();
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
        long now = _nowMs();
        if (PartyLootRules != null && drop.OwnerPartyId != 0 && Parties.PartyIdOf(player.CharacterId) == drop.OwnerPartyId)
            return Roll(player, drop, now, out taken);
        if (IsLockedFor(drop, player.CharacterId, now))
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

    /// <summary>
    /// R-4.3-02: inside the lock a party's drop lets in its members only (they roll before this is asked), or its killer alone
    /// once the party dissolved; a drop without a party lets in its killer.
    /// </summary>
    private bool IsLockedFor(LootDrop drop, long characterId, long nowMs)
    {
        if (!drop.IsLocked(nowMs))
            return false;
        if (PartyLootRules != null && drop.OwnerPartyId != 0 && Parties.MemberCount(drop.OwnerPartyId) > 0)
            return true;
        return characterId != drop.OwnerCharacterId;
    }

    /// <summary>
    /// A member's pickup of a party drop (spec 4.3 "M4 party loot"): every unit on the ground is rolled among the candidates
    /// (<see cref="PartyLoot.RollUnits"/>, seeded from the world's loot stream, the seed logged in <see cref="LootRolled"/>);
    /// each winner's bag takes what fits under its own key (<c>{drop}:p{n}:w{i}</c>, class 2) and the rest goes to it by mail
    /// (<c>...:mail</c>, the winner's class 1 save). The keys come from the run's unique drop key, so a part is applied and
    /// mailed once, ever. Units a failed mail send could not carry stay on the ground. <paramref name="taken"/> (the picker's
    /// <c>PickupResult.Taken</c>) is the units of the picker's own bag part only; every winner's part reaches the party as a
    /// <c>LootRollNotice</c> (#238 review).
    /// </summary>
    private PickupReason Roll(Player picker, LootDrop drop, long now, out int taken)
    {
        var rules = PartyLootRules!;
        long partyId = drop.OwnerPartyId;
        Span<long> candidates = stackalloc long[PartyLoot.MaxCandidates];
        int n = 0;
        foreach (long member in drop.EligibleAtKill)
        {
            if (n == candidates.Length)
                break;
            if (Parties.PartyIdOf(member) == partyId && PlayerOf(member) is { IsConnected: true } player
                && _inventory.IsAttached(member) && InInterest(player, drop))
                candidates[n++] = member;
        }
        if (n == 0)
            candidates[n++] = picker.CharacterId; // nobody eligible is here: the picker rolls alone (logged all the same)
        candidates = candidates.Slice(0, n);

        Span<long> debts = stackalloc long[n];
        for (int i = 0; i < n; i++)
            debts[i] = Debts?.DebtOf(partyId, candidates[i], now) ?? 0;
        long unitValue = rules.ValueSource == LootValueSource.PriceBase ? Math.Max(0, UnitValue(drop.Item)) : 0;
        ulong seed = Shared.Simulation.Items.PartyLoot.NextSeed(_rng);
        var weights = new long[n];
        Span<int> won = stackalloc int[n];
        Span<long> debtsAfter = stackalloc long[n];
        Shared.Simulation.Items.PartyLoot.RollUnits(debts, unitValue, rules.DebtHalfWeightGold, drop.Count, seed, weights, won,
            debtsAfter);

        string pickKey = string.Create(CultureInfo.InvariantCulture, $"{drop.DropKey}:p{drop.Pickups}");
        var wonOut = new int[n];
        var mailed = new int[n];
        var gone = new int[n];
        taken = 0;
        int left = 0; // the units that left the ground, every winner's
        for (int i = 0; i < n; i++)
        {
            wonOut[i] = won[i];
            if (won[i] == 0)
                continue;
            long winner = candidates[i];
            string bagKey = string.Create(CultureInfo.InvariantCulture, $"{pickKey}:w{i}");
            int toBag = Math.Min(won[i], _inventory.SpaceFor(winner, drop.Item, drop.Rarity));
            int added = toBag > 0
                ? _inventory.TryAdd(winner, drop.Item, drop.Rarity, toBag, bagKey, InventoryReason.LootPickup, drop.DropKey).Added
                : 0;
            int rest = won[i] - added;
            string? mailKey = null;
            if (rest > 0 && Mail != null)
            {
                mailKey = bagKey + ":mail";
                var draft = new MailDraft(winner, MailKind.LootOverflow, MailSubjects.LootOverflow, Array.Empty<MailArg>(),
                    new[] { new MailAttachment(drop.Item.Id, drop.Rarity, rest) }, 0, mailKey, drop.DropKey);
                if (Mail.Send(winner, draft).Status == MailSendStatus.Queued)
                {
                    mailed[i] = rest;
                    _inventory.RequestClass1Save(winner); // mail is class 1 (R-7.1-01): it rides the winner's next save
                }
            }
            gone[i] = added + mailed[i];
            left += gone[i];
            if (winner == picker.CharacterId)
                taken = added;
            if (gone[i] > 0)
            {
                // Announced once the winner's save holds it: the bag part's key, or the mail's when nothing went to the bag.
                _notices[added > 0 ? bagKey : mailKey!] = new PendingNotice(partyId, drop.Id, drop.DefId, drop.Rarity, winner, added,
                    mailed[i], now);
            }
            // The debt counts what the winner got; units that stayed on the ground (a refused mail) add nothing.
            Debts?.SetDebt(partyId, winner, debts[i] + unitValue * gone[i], now);
        }

        drop.Pickups++;
        drop.Count -= left;
        for (int i = 0; i < n; i++)
        {
            if (gone[i] > 0)
                _events?.Publish(new LootPickedUp(drop.Id, candidates[i], drop.DefId, gone[i], drop.Count), candidates[i],
                    picker.RegionIndex);
        }
        _events?.Publish(new LootRolled(partyId, drop.DropKey, drop.Id, drop.DefId, drop.Rarity, wonOut.Sum(), seed,
            picker.CharacterId, candidates.ToArray(), weights, wonOut, mailed), picker.CharacterId, picker.RegionIndex);
        if (drop.Count == 0)
            _taken.Add(drop); // leaves the world in RemoveTaken, after the phase 1 loop over the entities
        return left > 0 ? PickupReason.None : PickupReason.BagFull;
    }

    /// <summary>Within the square of half-size <see cref="InterestRadiusCm"/> around the player, as interest is (spec 2.4).</summary>
    private bool InInterest(Player player, LootDrop drop) =>
        Math.Abs(drop.XCm - player.XCm) <= InterestRadiusCm && Math.Abs(drop.YCm - player.YCm) <= InterestRadiusCm;

    /// <summary>
    /// A save committed an inventory operation (<c>InventoryService.OpCommitted</c>): a roll part waiting for it is announced.
    /// </summary>
    public void OnOpCommitted(long characterId, string opKey, InventoryReason reason)
    {
        if (reason == InventoryReason.LootPickup && _notices.Remove(opKey, out var notice))
            Announce(notice);
    }

    /// <summary>A save committed a mail send (<c>MailService.SendCommitted</c>): a roll part that went by mail only is announced.</summary>
    public void OnMailCommitted(string opKey)
    {
        if (_notices.Remove(opKey, out var notice))
            Announce(notice);
    }

    /// <summary>One winner's part to every connected member of the party (and the winner), its name as each sees it (rule 8).</summary>
    private void Announce(in PendingNotice notice)
    {
        var winner = PlayerOf(notice.Winner);
        int count = Parties.MembersOf(notice.PartyId, _members);
        bool winnerIsMember = _members.AsSpan(0, count).IndexOf(notice.Winner) >= 0;
        if (!winnerIsMember && count < _members.Length)
            _members[count++] = notice.Winner; // it left the party since: it still hears of its own winning
        for (int i = 0; i < count; i++)
        {
            if (PlayerOf(_members[i]) is not { IsConnected: true } recipient)
                continue;
            recipient.LootRollNotices.Add(new LootRollNotice
            {
                DropEntityId = notice.DropId,
                ItemNetId = notice.ItemNetId,
                Rarity = notice.Rarity,
                WinnerEntityId = winner?.Id ?? 0,
                WinnerName = winner != null ? DisplayIdentity.For(winner, recipient) : string.Empty,
                Units = (ushort)Math.Min(notice.Units, ushort.MaxValue),
                Mailed = (ushort)Math.Min(notice.Mailed, ushort.MaxValue),
            });
        }
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
        if (_notices.Count == 0)
            return;
        // A part whose save never committed (the winner's save faulted, the character left) is not announced late.
        _staleNotices.Clear();
        foreach (var pair in _notices)
        {
            if (now - pair.Value.AtMs >= _rules.DropExpiryMs)
                _staleNotices.Add(pair.Key);
        }
        foreach (string key in _staleNotices)
            _notices.Remove(key);
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

    /// <summary>One winner's part of a roll, waiting for the winner's save (user answer 2026-11-13: announced after the commit).</summary>
    private readonly record struct PendingNotice(long PartyId, uint DropId, ushort ItemNetId, byte Rarity, long Winner, int Units,
        int Mailed, long AtMs);
}
