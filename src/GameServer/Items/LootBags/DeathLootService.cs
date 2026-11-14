using System.Collections.Concurrent;
using GameServer.Entities;
using GameServer.Events;
using GameServer.Items.Mail;
using GameServer.Persistence;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Items;
using Shared.Util;
using Shared.World;

namespace GameServer.Items.LootBags;

/// <summary>
/// Death loot bags and the stolen mark (spec 4.3 R-4.3-03, 4.4 R-4.4-01, 8.2; M4 decision 8; WP-M4-9). Runs on the world thread;
/// items change only through <see cref="InventoryService"/> (rule 4) and mails only through the mail service.
/// <list type="bullet">
/// <item><b>The death transaction</b> (consumes <see cref="Died"/>, phase 11, idempotent per event id): a player who dies where
/// the region's <c>deathDrop</c> is on loses the Normal bag's unreserved stacks (<c>TryHoldOut</c>, reason <c>DeathDrop</c>);
/// the bag rows ride the owner's save, asked at once (<c>SaveReason.Death</c>), so the units that left and the bag commit
/// together: both or neither (class 1, the barrier by inclusion, R-7.1-02). The bag shows only once committed. At the world's
/// cap (<c>BagExpiry.maxBagsPerWorld</c>) nothing drops (user answer 2026-11-13).</item>
/// <item><b>Takes</b> (phase 1, players in entity id order): the owner takes back into the Normal bag, what does not fit by mail;
/// inside the owner window a party member collects for the owner (everything by mail to the owner, no mark) and anyone else
/// is locked out; after it any non-owner takes what fits with the stolen mark (<see cref="LootBagRules"/>). The rows leave
/// the bag in memory at once (the next taker of the tick sees the rest; a row with a take in flight cannot be taken again
/// until it committed), and the take rides the taker's save (<c>SaveReason.LootBag</c>), answered once it committed (rule 5).
/// TODO(M5): honor for a take and for a return (R-4.3-03, R-6.1-03).</item>
/// <item><b>Expiry</b> (phase 8): a bag past its time leaves the world and is closed off the tick in one standalone transaction
/// (<see cref="ILootBagExpirer"/>); <see cref="LootBagExpired"/> then counts the destroyed units (a sink).</item>
/// <item><b>Restart</b>: the open bags are loaded at world start (<see cref="Load"/>) and spawned again.</item>
/// </list>
/// </summary>
public sealed class DeathLootService : IEventConsumer<Died>, ILootBagBatchSource
{
    private readonly LootBagConfig _config;
    private readonly ItemRules _items;
    private readonly InventoryService _inventory;
    private readonly ILootBagWorld _world;
    private readonly EventBus? _events;
    private readonly string _runId;
    private readonly Func<long> _utcNowMs;
    private readonly long _rangeSq;
    private readonly Dictionary<string, DeathBag> _bags = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, DeathBag> _byEntity = new();
    private readonly Dictionary<long, Writes> _pending = new();
    private readonly Dictionary<long, (Guid SaveId, Writes Writes)> _inFlight = new();
    private readonly Queue<DeathBag> _expireQueue = new();
    private readonly List<(DeathBag Bag, long RetryAtMs)> _expireRetry = new();
    private readonly ConcurrentQueue<(DeathBag Bag, LootBagExpireResult? Result)> _expireDone = new();
    private Task? _expireTask;
    private bool _stopping;
    private long _nextExpiryCheckMs;
    private EventWatermark _watermark;

    public DeathLootService(LootBagConfig config, ItemRules items, InventoryService inventory, ILootBagWorld world, EventBus? events,
        string runId, Func<long> utcNowMs)
    {
        _config = config;
        _items = items;
        _inventory = inventory;
        _world = world;
        _events = events;
        _runId = runId;
        _utcNowMs = utcNowMs;
        long range = config.PickupRangeCm;
        _rangeSq = range * range;
    }

    /// <summary>Asks a character's save at once (the scheduler's <c>RequestSave</c>; set by the SessionManager).</summary>
    public Action<long, SaveReason>? RequestSave { get; set; }

    /// <summary>The mail side of a take (the world's mail service); null: nothing is mailed (the owner's overflow stays in the bag).</summary>
    public ILootBagMail? Mail { get; set; }

    /// <summary>Closes expired bags off the world thread (<see cref="LootBagRepository"/>); null: they are closed in memory at once (tests).</summary>
    public ILootBagExpirer? Expirer { get; set; }

    public LootBagConfig Config => _config;

    /// <summary>Bags that exist now (being created, open or expiring).</summary>
    public int Count => _bags.Count;

    /// <summary>Deaths that dropped nothing because the world held <c>BagExpiry.maxBagsPerWorld</c> bags.</summary>
    public long DropsOverCap { get; private set; }

    public void Subscribe(EventBus events) => events.Subscribe<Died>(this);

    /// <summary>Starts with the bags still open in the database (world start): each is spawned again.</summary>
    public void Load(IEnumerable<LootBagRecord> open)
    {
        foreach (var record in open)
        {
            if (_bags.ContainsKey(record.BagId))
                continue;
            var bag = new DeathBag(record.BagId, record.OwnerCharacterId, record.RegionId, record.XCm, record.YCm, record.HeightCm,
                record.CreatedUtcMs, record.OwnerWindowUntilUtcMs, record.ExpiresUtcMs);
            foreach (var item in record.Items)
            {
                // A row the tables lost stays in the database (and is destroyed at the expiry); it is never shown or taken.
                if (_items.TryGetItem(item.DefId, out var def))
                    bag.Rows.Add(new BagRow(item.Index, def, (byte)item.Rarity, item.Stolen, item.StolenFrom, item.StolenAtMs) { Count = item.Count });
                else
                    bag.Lost.Add(item);
            }
            _bags.Add(bag.Id, bag);
            Open(bag);
        }
    }

    /// <summary>The bag an entity shows, or null.</summary>
    public DeathBag? Find(uint bagEntityId) => _byEntity.TryGetValue(bagEntityId, out var bag) ? bag : null;

    /// <summary>A bag by its id (tests and tools), or null.</summary>
    public DeathBag? FindById(string bagId) => _bags.TryGetValue(bagId, out var bag) ? bag : null;

    // ---------------------------------------------------------------------------------------------------------------------
    // The death transaction.

    public void Handle(in EventEnvelope envelope, in Died payload)
    {
        if (!_watermark.TryAccept(envelope))
            return; // a re-dispatched death drops nothing twice
        if (_world.FindEntity(payload.EntityId) is not Player player || !_inventory.IsAttached(player.CharacterId))
            return; // a monster, or a character no longer here
        // TODO(M5): a death by a player (PvP, the first class change) and the guards' confiscation (deathDrop confiscate,
        // R-6.3-05); no player damages another in M4 (PvpRules), so every death here is a PvE death.
        if (_world.RegionOf(player) is not { } region || region.Flags.DeathDrop != DeathDrop.On
            || !_config.TryGetWindowMs(region.AreaType, out int windowMs))
            return;
        long owner = player.CharacterId;
        var units = new List<ReserveItem>();
        byte code = (byte)_items.NormalBag.Code;
        var bag0 = _inventory.Current(owner);
        for (int slot = 0; slot < bag0.SlotCount; slot++)
        {
            var stack = bag0[slot];
            if (stack.IsEmpty || stack.IsInstance)
                continue; // the Normal bag holds stacks only (R-4.1-02); an instance would never be in it
            var at = new InventoryLocation(code, slot);
            int free = _inventory.Unreserved(owner, at); // units a trade or a craft holds stay with the character
            if (free > 0)
                units.Add(new ReserveItem(at, free));
        }
        if (units.Count == 0)
            return; // an empty bag drops nothing
        if (_bags.Count >= _config.Expiry.MaxBagsPerWorld)
        {
            DropsOverCap++;
            _events?.Publish(new LootBagNotDropped(owner, units.Sum(u => u.Count)), owner, player.RegionIndex);
            return;
        }

        string bagId = $"death:{_runId}:{envelope.EventId}";
        var held = new List<EscrowItemRow>(units.Count);
        if (_inventory.TryHoldOut(owner, units, 0, bagId, InventoryReason.DeathDrop, $"lootBag:{bagId}", held, out _) == null)
            return; // nothing changed (a repeated key, or the bag changed under the request)
        long now = _utcNowMs();
        var bag = new DeathBag(bagId, owner, region.RegionId, player.XCm, player.YCm, player.HeightCm, now,
            LootBagRules.WindowUntil(now, windowMs), LootBagRules.ExpiresAt(now, _config.Expiry.ExpiryMs)) { Phase = BagPhase.Creating };
        for (int i = 0; i < held.Count; i++)
        {
            var row = held[i];
            _items.TryGetItem(row.DefId, out var def);
            bag.Rows.Add(new BagRow(i, def, (byte)row.Rarity, row.Stolen, row.StolenFrom, row.StolenAtMs) { Count = row.Count });
        }
        _bags.Add(bagId, bag);
        PendingOf(owner).Creates.Add(bag);
        RequestSave?.Invoke(owner, SaveReason.Death);
    }

    // ---------------------------------------------------------------------------------------------------------------------
    // Requests.

    /// <summary>
    /// Phase 1: one loot bag request of <paramref name="player"/>. Writes the answer of an open, a close or a refusal now; a take
    /// that was applied is answered once its save committed.
    /// </summary>
    public void Request(Player player, uint requestId, uint bagEntityId, LootBagOp op, byte row)
    {
        var answers = player.LootBags;
        if (!answers.FirstSight(requestId))
            return; // a resend: the first one is (or will be) answered
        if (op == LootBagOp.Close)
        {
            if (answers.OpenBagEntityId == bagEntityId)
                answers.OpenBagEntityId = 0;
            Answer(player, requestId, bagEntityId, op, LootBagReason.None);
            return;
        }
        var reason = Check(player, bagEntityId, out var bag, out var access);
        if (reason != LootBagReason.None)
        {
            Answer(player, requestId, bagEntityId, op, reason);
            return;
        }
        if (op == LootBagOp.Open)
        {
            answers.OpenBagEntityId = bagEntityId;
            answers.SentBagRevision = -1; // the contents go out in phase 10
            Answer(player, requestId, bagEntityId, op, LootBagReason.None);
            return;
        }
        reason = Take(player, requestId, bag!, access, op, row);
        if (reason != LootBagReason.None)
            Answer(player, requestId, bagEntityId, op, reason);
    }

    /// <summary>Alive, the bag in sight and in reach, open and not expired, and the viewer may use it (not locked).</summary>
    private LootBagReason Check(Player player, uint bagEntityId, out DeathBag? bag, out LootBagAccess access)
    {
        access = LootBagAccess.None;
        bag = null;
        if (player.Combatant is { IsAlive: false })
            return LootBagReason.PickupWhileDead;
        if (!_inventory.IsAttached(player.CharacterId))
            return LootBagReason.InventoryUnavailable;
        if (!_byEntity.TryGetValue(bagEntityId, out bag) || bag.Entity == null || !_world.CanSee(player, bag.Entity))
            return LootBagReason.BagNotFound;
        long dx = bag.XCm - player.XCm, dy = bag.YCm - player.YCm;
        if (dx * dx + dy * dy > _rangeSq)
            return LootBagReason.OutOfRange;
        long now = _utcNowMs();
        if (bag.Phase != BagPhase.Open || LootBagRules.IsExpired(now, bag.ExpiresMs))
            return LootBagReason.BagExpired;
        access = AccessOf(player.CharacterId, bag, now);
        return access == LootBagAccess.Locked ? LootBagReason.BagLocked : LootBagReason.None;
    }

    private LootBagAccess AccessOf(long viewer, DeathBag bag, long now) =>
        LootBagRules.Access(viewer, bag.Owner, viewer != bag.Owner && _world.SameParty(viewer, bag.Owner), now, bag.WindowUntilMs);

    private LootBagReason Take(Player player, uint requestId, DeathBag bag, LootBagAccess access, LootBagOp op, byte rowIndex)
    {
        long taker = player.CharacterId;
        var rows = new List<BagRow>();
        foreach (var row in bag.Rows)
        {
            if (row.Count > 0 && row.Busy == 0 && row.Def != null && (op == LootBagOp.TakeAll || row.Index == rowIndex))
                rows.Add(row);
        }
        if (rows.Count == 0)
        {
            if (op == LootBagOp.TakeOne && !bag.Rows.Exists(r => r.Index == rowIndex && r.Count > 0))
                return bag.Rows.Exists(r => r.Count > 0) ? LootBagReason.BagNotFound : LootBagReason.BagEmpty;
            return LootBagReason.BagEmpty; // empty, or every row asked for is being taken by a save in flight
        }
        long now = _utcNowMs();
        var outcome = access switch
        {
            LootBagAccess.Own => LootBagTakeOutcome.Owner,
            LootBagAccess.CollectForOwner => LootBagTakeOutcome.ForOwner,
            _ => LootBagTakeOutcome.Stolen,
        };

        // What each row gives: the stack as it goes (marked for a stranger), then the units the taker's bag takes and the units
        // that go by mail (the owner's overflow; everything a party member collects).
        var stacks = new List<EscrowItemRow>(rows.Count);
        foreach (var row in rows)
        {
            var stack = new ItemStack((ushort)row.Def!.NetId, row.Rarity, row.Stolen, row.Count, 0, row.StolenFrom, row.StolenAtMs);
            if (outcome == LootBagTakeOutcome.Stolen)
                stack = LootBagRules.Mark(stack, bag.Owner, now);
            stacks.Add(new EscrowItemRow(row.Def.Id, stack.Rarity, stack.Stolen, stack.Count, 0, false, null, stack.StolenFrom, stack.StolenAtMs));
        }
        var fit = outcome == LootBagTakeOutcome.ForOwner ? new int[rows.Count] : _inventory.FitCounts(taker, stacks);
        var toBag = new List<EscrowItemRow>();
        var toMail = new List<(int Row, EscrowItemRow Stack)>();
        for (int i = 0; i < rows.Count; i++)
        {
            int taken = Math.Min(fit[i], stacks[i].Count);
            if (taken > 0)
                toBag.Add(stacks[i] with { Count = taken });
            if (outcome != LootBagTakeOutcome.Stolen && stacks[i].Count > taken)
                toMail.Add((i, stacks[i] with { Count = stacks[i].Count - taken }));
        }

        string opKey = $"bag:{_runId}:{player.Id}:{player.Connection}:{requestId}";
        string source = $"lootBag:{bag.Id}";
        var drafts = new List<MailDraft>();
        if (toMail.Count > 0)
        {
            if (Mail == null)
            {
                if (outcome == LootBagTakeOutcome.ForOwner)
                    return LootBagReason.InventoryUnavailable; // collecting for the owner needs mail
                toMail.Clear(); // the owner's overflow stays in the bag
            }
            else
            {
                int perMail = Mail.AttachmentsPerMail;
                int mails = LootBagRules.MailCount(toMail.Count, perMail);
                for (int m = 0; m < mails; m++)
                {
                    var (start, count) = LootBagRules.MailSlice(toMail.Count, perMail, m);
                    var attachments = new List<MailAttachment>(count);
                    for (int i = start; i < start + count; i++)
                    {
                        var s = toMail[i].Stack;
                        attachments.Add(new MailAttachment(s.DefId, (byte)s.Rarity, s.Count, s.Stolen, false, 0, null, s.StolenFrom, s.StolenAtMs));
                    }
                    var draft = new MailDraft(bag.Owner, MailKind.LootBagReturn, MailSubjects.LootBagReturn, Array.Empty<MailArg>(),
                        attachments, 0, $"{opKey}:m{m}", source);
                    if (!Mail.CanSend(taker, draft))
                    {
                        if (outcome == LootBagTakeOutcome.ForOwner)
                            return LootBagReason.InventoryUnavailable;
                        drafts.Clear();
                        toMail.Clear(); // the owner's overflow stays in the bag
                        break;
                    }
                    drafts.Add(draft);
                }
            }
        }
        if (toBag.Count == 0 && toMail.Count == 0)
            return LootBagReason.BagFull;

        if (toBag.Count > 0
            && _inventory.TryReceive(taker, toBag, 0, opKey, InventoryReason.LootBagTake, source, bag.Owner, 0, out var refusal) == null)
            return refusal == TransferRefusal.BagFull ? LootBagReason.BagFull : LootBagReason.BagEmpty;
        foreach (var draft in drafts)
        {
            if (!Mail!.Send(taker, draft))
                throw new InvalidOperationException($"loot bag take {opKey}: a mail checked before was refused");
        }

        // Apply to the bag: the rows leave it now; the take rides the taker's save.
        var take = new BagTake(opKey, bag, taker, player.Connection, requestId, bag.Entity!.Id, op, outcome, now);
        var mailedOf = new int[rows.Count];
        foreach (var (row, stack) in toMail)
            mailedOf[row] = stack.Count;
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            int taken = Math.Min(fit[i], stacks[i].Count);
            int mailed = mailedOf[i];
            if (taken + mailed == 0)
                continue;
            take.Rows.Add(new TakenRow(row, row.Count, taken, mailed, stacks[i]));
            row.Count -= taken + mailed;
            row.Busy++;
        }
        bag.TakesInFlight++;
        bag.Revision++;
        PendingOf(taker).Takes.Add(take);
        RequestSave?.Invoke(taker, SaveReason.LootBag);
        return LootBagReason.None;
    }

    private void Answer(Player player, uint requestId, uint bagEntityId, LootBagOp op, LootBagReason reason) =>
        player.LootBags.Messages.Add(ReliableMessages.Write(ReliableMessageId.LootBagResult,
            new LootBagResult { RequestId = requestId, BagEntityId = bagEntityId, Op = op, Reason = reason }));

    // ---------------------------------------------------------------------------------------------------------------------
    // Saves (ILootBagBatchSource).

    public LootBagBatch? TakeBatch(long characterId, Guid saveId)
    {
        if (!_pending.Remove(characterId, out var writes))
            return null;
        var creates = writes.Creates.Select(Record).ToList();
        var takes = writes.Takes.Select(t => new LootBagTakeWrite(t.OpKey, t.Bag.Id, t.Taker, t.Outcome,
            t.Rows.Select(r => new LootBagTakenRow(r.Row.Index, r.Row.Def!.Id, r.Row.Rarity, r.Seen, r.Taken + r.Mailed)).ToList(),
            t.AtMs)).ToList();
        _inFlight[characterId] = (saveId, writes);
        return new LootBagBatch(creates, takes);
    }

    public void Committed(long characterId, Guid saveId)
    {
        if (!_inFlight.TryGetValue(characterId, out var inFlight) || inFlight.SaveId != saveId)
            return;
        _inFlight.Remove(characterId);
        foreach (var bag in inFlight.Writes.Creates)
        {
            if (bag.Phase != BagPhase.Creating)
                continue;
            Open(bag);
            var items = Counts(bag.Rows.Select(r => (r.Def, r.Rarity, r.Count)));
            _events?.Publish(new LootBagCreated(bag.Id, bag.Entity!.Id, bag.Owner, bag.RegionId, items, items.Sum(i => i.Count),
                Value(bag.Rows.Select(r => (r.Def, r.Count)))), bag.Owner, bag.Entity.RegionIndex);
        }
        foreach (var take in inFlight.Writes.Takes)
            Settle(take);
    }

    /// <summary>
    /// The character's session ended without its save (a fault or lost ownership): writes no save took are undone in memory (the
    /// database never saw them); a bag whose writes were in a save in flight is unknown until a restart, and leaves the world.
    /// </summary>
    public void Forget(long characterId)
    {
        if (_pending.Remove(characterId, out var pending))
        {
            foreach (var bag in pending.Creates)
                _bags.Remove(bag.Id); // never committed, never shown; the character's memory is dropped with it
            foreach (var take in pending.Takes)
            {
                foreach (var row in take.Rows)
                {
                    row.Row.Count += row.Taken + row.Mailed;
                    row.Row.Busy--;
                }
                take.Bag.TakesInFlight--;
                take.Bag.Revision++;
            }
        }
        if (_inFlight.Remove(characterId, out var inFlight))
        {
            foreach (var bag in inFlight.Writes.Creates)
                Drop(bag);
            foreach (var take in inFlight.Writes.Takes)
                Drop(take.Bag);
        }
    }

    // ---------------------------------------------------------------------------------------------------------------------
    // Expiry (phase 8) and its results.

    /// <summary>Phase 8: bags past their time with no take in flight leave the world and are closed off the tick.</summary>
    public void ExpireDue()
    {
        long now = _utcNowMs();
        if (now < _nextExpiryCheckMs)
            return;
        _nextExpiryCheckMs = now + _config.Expiry.ExpiryCheckIntervalMs;
        List<DeathBag>? due = null;
        foreach (var bag in _bags.Values)
        {
            if (bag.Phase == BagPhase.Open && bag.TakesInFlight == 0 && LootBagRules.IsExpired(now, bag.ExpiresMs))
                (due ??= new List<DeathBag>()).Add(bag);
        }
        if (due == null)
            return;
        foreach (var bag in due.OrderBy(b => b.ExpiresMs).ThenBy(b => b.Id, StringComparer.Ordinal))
        {
            bag.Phase = BagPhase.Expiring;
            Despawn(bag);
            if (Expirer == null)
            {
                var rows = bag.Rows.Where(r => r.Count > 0).Select(r => new LootBagItemRow(r.Index, r.Def!.Id, r.Rarity, r.Count, r.Stolen))
                    .Concat(bag.Lost).ToList();
                _expireDone.Enqueue((bag, new LootBagExpireResult(LootBagExpireStatus.Expired, rows)));
            }
            else
                _expireQueue.Enqueue(bag);
        }
        PumpExpiry();
    }

    /// <summary>Phase 1: applies finished expiries (the events, a sink) and retries those whose outcome was unknown.</summary>
    public void Tick()
    {
        while (_expireDone.TryDequeue(out var done))
        {
            var bag = done.Bag;
            if (done.Result is not { } result)
            {
                _expireRetry.Add((bag, _utcNowMs() + _config.Expiry.ExpiryRetryMs));
                continue;
            }
            _bags.Remove(bag.Id);
            bag.Phase = BagPhase.Closed;
            OwnBagsChanged(bag.Owner);
            if (result.Status == LootBagExpireStatus.Gone)
                continue; // emptied by takes elsewhere: nothing destroyed here
            var items = Counts(result.Items.Select(i => (Def(i.DefId), (byte)i.Rarity, i.Count)));
            _events?.Publish(new LootBagExpired(bag.Id, bag.EntityId, bag.Owner, items, result.Items.Sum(i => i.Count),
                Value(result.Items.Select(i => (Def(i.DefId), i.Count)))), bag.Owner);
        }
        long now = _utcNowMs();
        for (int i = _expireRetry.Count - 1; i >= 0; i--)
        {
            if (_expireRetry[i].RetryAtMs > now)
                continue;
            _expireQueue.Enqueue(_expireRetry[i].Bag);
            _expireRetry.RemoveAt(i);
        }
        PumpExpiry();
    }

    private void PumpExpiry()
    {
        if (_stopping || Expirer == null || _expireQueue.Count == 0 || _expireTask is { IsCompleted: false })
            return;
        var bag = _expireQueue.Dequeue();
        var expirer = Expirer;
        long at = _utcNowMs();
        _expireTask = Task.Run(async () =>
        {
            LootBagExpireResult? result;
            try
            {
                result = await expirer.ExpireAsync(bag.Id, at);
            }
            catch (Exception)
            {
                result = null; // unknown: tried again with the same key (idempotent)
            }
            _expireDone.Enqueue((bag, result));
        });
    }

    /// <summary>Shutdown: starts no further expiry and returns the one in flight; a queued one runs again next start.</summary>
    public Task StopAsync()
    {
        _stopping = true;
        return _expireTask ?? Task.CompletedTask;
    }

    // ---------------------------------------------------------------------------------------------------------------------
    // Views for phase 10.

    /// <summary>
    /// The contents of the bag <paramref name="player"/> has open, or null when nothing needs sending; <paramref name="closed"/>
    /// when the bag is gone or out of reach (the client closes it; the player's open bag is cleared).
    /// </summary>
    public LootBagContent? ContentFor(Player player)
    {
        var answers = player.LootBags;
        uint entityId = answers.OpenBagEntityId;
        if (entityId == 0)
            return null;
        bool gone = !_byEntity.TryGetValue(entityId, out var bag) || bag.Phase != BagPhase.Open || bag.Entity == null
                    || !_world.CanSee(player, bag.Entity) || Far(player, bag);
        if (gone)
        {
            answers.OpenBagEntityId = 0;
            return new LootBagContent { BagEntityId = entityId, Revision = ++answers.ContentRevision, Closed = true };
        }
        if (answers.SentBagRevision == bag!.Revision)
            return null;
        answers.SentBagRevision = bag.Revision;
        long now = _utcNowMs();
        return new LootBagContent
        {
            BagEntityId = entityId,
            Revision = ++answers.ContentRevision,
            Access = AccessOf(player.CharacterId, bag, now),
            OwnerWindowLeftMs = LootBagRules.MsLeft(now, bag.WindowUntilMs),
            ExpiresInMs = LootBagRules.MsLeft(now, bag.ExpiresMs),
            Rows = bag.Rows.Where(r => r.Count > 0 && r.Def != null).Select(r => new LootBagRowEntry
            {
                Index = (byte)r.Index, ItemNetId = (ushort)r.Def!.NetId, Rarity = r.Rarity, Stolen = r.Stolen,
                Count = (ushort)Math.Min(ushort.MaxValue, r.Count),
            }).ToArray(),
        };
    }

    /// <summary>The player's own open bags, newest first, at most <c>BagExpiry.ownBagsListedMax</c>.</summary>
    public OwnLootBags OwnBags(Player player)
    {
        long now = _utcNowMs();
        var bags = _bags.Values.Where(b => b.Owner == player.CharacterId && b.Phase == BagPhase.Open && b.Entity != null)
            .OrderByDescending(b => b.CreatedMs).ThenBy(b => b.Id, StringComparer.Ordinal).Take(_config.Expiry.OwnBagsListedMax)
            .Select(b => new OwnLootBagEntry
            {
                BagEntityId = b.Entity!.Id, XCm = b.XCm, YCm = b.YCm,
                OwnerWindowLeftMs = LootBagRules.MsLeft(now, b.WindowUntilMs), ExpiresInMs = LootBagRules.MsLeft(now, b.ExpiresMs),
                Units = (ushort)Math.Min(ushort.MaxValue, b.Rows.Sum(r => r.Count)),
            }).ToArray();
        return new OwnLootBags { Revision = ++player.LootBags.OwnBagsRevision, Bags = bags };
    }

    // ---------------------------------------------------------------------------------------------------------------------
    // Internals.

    private bool Far(Player player, DeathBag bag)
    {
        long dx = bag.XCm - player.XCm, dy = bag.YCm - player.YCm;
        return dx * dx + dy * dy > _rangeSq;
    }

    /// <summary>A take committed: the rows are free again, the events, the answer, and the bag closes when nothing is left.</summary>
    private void Settle(BagTake take)
    {
        var bag = take.Bag;
        foreach (var row in take.Rows)
            row.Row.Busy--;
        bag.TakesInFlight--;
        bag.Revision++;
        var player = _world.PlayerOf(take.Taker);
        int taken = take.Rows.Sum(r => r.Taken), mailed = take.Rows.Sum(r => r.Mailed);
        var items = Counts(take.Rows.Select(r => (r.Row.Def, r.Row.Rarity, r.Taken + r.Mailed)));
        _events?.Publish(new LootBagPickedUp(bag.Id, take.BagEntityId, take.Taker, bag.Owner, take.Outcome, items, taken, mailed), take.Taker,
            player?.RegionIndex ?? 0);
        if (take.Outcome == LootBagTakeOutcome.Stolen)
        {
            foreach (var row in take.Rows)
            {
                if (row.Taken > 0)
                    _events?.Publish(new ItemStolen(take.Taker, row.Given.StolenFrom, (ushort)row.Row.Def!.NetId, row.Row.Rarity, row.Taken,
                        row.Given.StolenAtMs, bag.Id), take.Taker, player?.RegionIndex ?? 0);
            }
        }
        if (player != null && player.Connection == take.Connection)
        {
            player.LootBags.Messages.Add(ReliableMessages.Write(ReliableMessageId.LootBagResult, new LootBagResult
            {
                RequestId = take.RequestId, BagEntityId = take.BagEntityId, Op = take.Op, Reason = LootBagReason.None,
                Taken = (ushort)Math.Min(ushort.MaxValue, taken), Mailed = (ushort)Math.Min(ushort.MaxValue, mailed),
                Stolen = take.Outcome == LootBagTakeOutcome.Stolen,
            }));
        }
        OwnBagsChanged(bag.Owner);
        if (bag.Phase == BagPhase.Open && bag.TakesInFlight == 0 && bag.Rows.TrueForAll(r => r.Count == 0) && bag.Lost.Count == 0)
        {
            // Emptied: the last take's save closed it in the database too (a take closes the bag when no row is left).
            bag.Phase = BagPhase.Closed;
            Despawn(bag);
            _bags.Remove(bag.Id);
        }
    }

    /// <summary>A committed bag becomes visible: spawned at its spot and listed for its owner.</summary>
    private void Open(DeathBag bag)
    {
        bag.Phase = BagPhase.Open;
        bag.Entity = _world.AddBag(bag.Id, bag.XCm, bag.YCm, bag.HeightCm);
        bag.EntityId = bag.Entity.Id;
        _byEntity[bag.Entity.Id] = bag;
        OwnBagsChanged(bag.Owner);
    }

    private void Despawn(DeathBag bag)
    {
        if (bag.Entity == null)
            return;
        _byEntity.Remove(bag.Entity.Id);
        _world.RemoveBag(bag.Entity);
        bag.Entity = null;
        OwnBagsChanged(bag.Owner);
    }

    /// <summary>A bag whose write was in a save in flight with a faulted character: unknown here until a restart reads it.</summary>
    private void Drop(DeathBag bag)
    {
        bag.Phase = BagPhase.Unknown;
        Despawn(bag);
        _bags.Remove(bag.Id);
    }

    private void OwnBagsChanged(long owner)
    {
        if (_world.PlayerOf(owner) is { } player)
            player.LootBags.OwnBagsDirty = true;
    }

    private static LootBagRecord Record(DeathBag bag) => new(bag.Id, bag.Owner, bag.RegionId, bag.XCm, bag.YCm, bag.HeightCm, bag.CreatedMs,
        bag.WindowUntilMs, bag.ExpiresMs, bag.Rows.Select(r => new LootBagItemRow(r.Index, r.Def!.Id, r.Rarity, r.Count, r.Stolen, r.StolenFrom,
            r.StolenAtMs)).ToList());

    private ItemDef? Def(string defId) => _items.TryGetItem(defId, out var def) ? def : null;

    private static List<LootBagItemCount> Counts(IEnumerable<(ItemDef? Def, byte Rarity, int Count)> rows) =>
        rows.Where(r => r.Def != null && r.Count > 0).Select(r => new LootBagItemCount((ushort)r.Def!.NetId, r.Rarity, r.Count)).ToList();

    private long Value(IEnumerable<(ItemDef? Def, int Count)> rows) =>
        rows.Where(r => r.Def != null).Sum(r => (long)_config.BaseGold(r.Def!.Id) * r.Count);

    private Writes PendingOf(long characterId)
    {
        if (!_pending.TryGetValue(characterId, out var writes))
            _pending.Add(characterId, writes = new Writes());
        return writes;
    }

    private sealed class Writes
    {
        public List<DeathBag> Creates { get; } = new();
        public List<BagTake> Takes { get; } = new();
    }

    private sealed class BagTake(string opKey, DeathBag bag, long taker, int connection, uint requestId, uint bagEntityId, LootBagOp op,
        LootBagTakeOutcome outcome, long atMs)
    {
        public string OpKey { get; } = opKey;
        public DeathBag Bag { get; } = bag;
        public long Taker { get; } = taker;
        public int Connection { get; } = connection;
        public uint RequestId { get; } = requestId;
        public uint BagEntityId { get; } = bagEntityId;
        public LootBagOp Op { get; } = op;
        public LootBagTakeOutcome Outcome { get; } = outcome;
        public long AtMs { get; } = atMs;
        public List<TakenRow> Rows { get; } = new();
    }

    /// <summary>One row a take moved: the units before it (the database guard), into the bag, by mail, and the stack it gave.</summary>
    private readonly record struct TakenRow(BagRow Row, int Seen, int Taken, int Mailed, EscrowItemRow Given);
}

/// <summary>Where a bag is in its life (memory only).</summary>
public enum BagPhase : byte
{
    /// <summary>The death's save is not committed yet: not shown, nothing may be taken.</summary>
    Creating,
    Open,
    /// <summary>Past its time, leaving the world: being closed off the tick.</summary>
    Expiring,
    Closed,
    /// <summary>A write of it was in a save lost with its character: left alone until a restart reads the database.</summary>
    Unknown,
}

/// <summary>A death loot bag as the world keeps it (WP-M4-9).</summary>
public sealed class DeathBag(string id, long owner, string regionId, int xCm, int yCm, int heightCm, long createdMs, long windowUntilMs,
    long expiresMs)
{
    public string Id { get; } = id;
    public long Owner { get; } = owner;
    public string RegionId { get; } = regionId;
    public int XCm { get; } = xCm;
    public int YCm { get; } = yCm;
    public int HeightCm { get; } = heightCm;
    public long CreatedMs { get; } = createdMs;
    public long WindowUntilMs { get; } = windowUntilMs;
    public long ExpiresMs { get; } = expiresMs;
    public List<BagRow> Rows { get; } = new();

    /// <summary>Rows whose item the tables no longer know (loaded at start): never shown or taken, destroyed at the expiry.</summary>
    public List<LootBagItemRow> Lost { get; } = new();

    public BagPhase Phase { get; set; }
    public LootBag? Entity { get; set; }

    /// <summary>The entity id the bag had (kept for the events once it left the world).</summary>
    public uint EntityId { get; set; }

    /// <summary>Takes applied and not yet committed (they hold rows; the bag does not expire meanwhile).</summary>
    public int TakesInFlight { get; set; }

    /// <summary>Counts changes, so an open window gets new contents only after one.</summary>
    public long Revision { get; set; }

    public int Units => Rows.Sum(r => r.Count);
}

/// <summary>One stack in a bag (memory): the units left, and takes in flight on it.</summary>
public sealed class BagRow(int index, ItemDef? def, byte rarity, bool stolen, long stolenFrom, long stolenAtMs)
{
    public int Index { get; } = index;
    public ItemDef? Def { get; } = def;
    public byte Rarity { get; } = rarity;
    public bool Stolen { get; } = stolen;
    public long StolenFrom { get; } = stolenFrom;
    public long StolenAtMs { get; } = stolenAtMs;
    public int Count { get; set; }

    /// <summary>Takes in flight on this row: it cannot be taken again until they committed (the database guard counts on that).</summary>
    public int Busy { get; set; }
}
