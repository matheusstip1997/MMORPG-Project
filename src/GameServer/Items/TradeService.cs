using GameServer.Events;
using GameServer.Persistence;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Items;

namespace GameServer.Items;

/// <summary>A player as the trade sees it this tick (read from the world, never from the client).</summary>
public readonly record struct TradeParticipant(long CharacterId, uint EntityId, int XCm, int YCm, bool Alive, bool InCombat,
    bool Connected);

/// <summary>What <see cref="TradeService"/> needs of the world (implemented by <c>GameWorld</c>; fakes in tests).</summary>
public interface ITradeWorld
{
    long NowMs { get; }

    bool TryGetCharacter(long characterId, out TradeParticipant participant);

    /// <summary>
    /// The player entity <paramref name="entityId"/> as <paramref name="observerCharacterId"/> may know it: false for a player the
    /// observer is not sent (outside its interest; rule 9), so a refusal never reveals it.
    /// </summary>
    bool TryGetEntity(uint entityId, long observerCharacterId, out TradeParticipant participant);

    /// <summary>The name <paramref name="observerCharacterId"/> sees for <paramref name="characterId"/> (<c>DisplayIdentity</c>, rule 8).</summary>
    string NameFor(long characterId, long observerCharacterId);

    /// <summary>Saves both characters in one transaction (<c>SaveScheduler.RequestTransferSave</c>); false when either cannot be linked.</summary>
    bool RequestTransferSave(TransferRecord transfer);

    /// <summary>Undoes a link whose swap was refused (nothing applied): the two characters save alone again.</summary>
    void AbortTransferSave(TransferRecord transfer);
}

/// <summary>A message for one character's connection, sent in phase 10 (<c>TradeSender</c>).</summary>
public readonly record struct TradeMessage(long CharacterId, TradeResult? Result, TradeState? State);

/// <summary>
/// The trade window (spec 4.5 "M4 trade window", R-4.5-01, -02, R-4.1-04; M4 decision 5; WP-M4-4). World thread, phase 1
/// requests in entity id order, <see cref="Tick"/> once per tick. Every number is a <see cref="TradeSettings"/> placeholder.
/// <list type="bullet">
/// <item>One open trade (or invite) per player; the two within <c>rangeCm</c>, both alive and out of combat, checked at the
/// invite, the accept and every tick until the swap; a disconnect, a death, combat, distance or a timeout cancels it, and a
/// cancel starts <c>cancelCooldownMs</c> for both.</item>
/// <item>Offered units are reserved in the giver's bag (<c>InventoryService.Reserve</c>, key <c>trade:{id}:{character}</c>), so
/// nothing else can move, sell or use them while the window is open. Sell-only, quest, stolen or tainted, non-tradable,
/// equipped and otherwise reserved units are refused, each with its own reason.</item>
/// <item>Every offer change bumps the revision and clears both locks and confirms. Lock and confirm name the revision the
/// player saw; a stale one is <c>TradeChanged</c>.</item>
/// <item>When both confirmed, everything is re-validated and swapped at once in memory
/// (<c>InventoryService.TryTransfer</c>, reason <c>Trade</c>), then both characters are saved in <b>one</b> transaction
/// (<c>SaveScheduler.RequestTransferSave</c>, with both pending batches: the barrier by inclusion over two characters). Both
/// confirms are answered, <see cref="TradeCompleted"/> published and the window closed only once that save committed (rule 5).
/// A fault of the save drops both sessions; nothing is in the database.</item>
/// </list>
/// TODO(open) decision 14: the level that unlocks trading (no <c>UnlockSchedule</c> row until WP-M4-11).
/// TODO(open) decision 17: large transfers flagged (R-4.5-02, spec 7.5); TODO(open): a trade rate limit beyond the cooldown (7.5).
/// TODO(open): the cancel cooldown on both sides, with an invite making its target busy, lets one player keep another from
/// trading (a design question put to the user; unchanged until it is answered).
/// </summary>
public sealed class TradeService
{
    private readonly InventoryService _inventory;
    private readonly ITradeWorld _world;
    private readonly EventBus? _events;
    private readonly string _runId;
    private TradeSettings _settings;
    private readonly Dictionary<long, Trade> _byCharacter = new();
    private readonly List<Trade> _trades = new(); // creation order = id order (rule 6)
    private readonly Dictionary<long, long> _cooldownUntil = new();
    private readonly List<long> _expired = new();
    private readonly List<TradeMessage> _outbox = new();
    private uint _nextId;

    public TradeService(InventoryService inventory, ITradeWorld world, TradeSettings settings, EventBus? events, string runId)
    {
        _inventory = inventory;
        _world = world;
        _settings = settings;
        _events = events;
        _runId = runId;
    }

    /// <summary>Messages waiting for phase 10; the sender drains it.</summary>
    public List<TradeMessage> Outbox => _outbox;

    public int OpenCount => _trades.Count;

    public bool IsTrading(long characterId) => _byCharacter.ContainsKey(characterId);

    /// <summary>Phase 1: one request of <paramref name="characterId"/>. A refusal or an acknowledgement goes to the outbox.</summary>
    public void Handle(long characterId, uint requestId, TradeAction action, uint targetEntityId, byte container, ushort slot,
        ushort count, byte offerIndex, long gold, uint revision)
    {
        var reason = action switch
        {
            TradeAction.Invite => Invite(characterId, targetEntityId),
            TradeAction.Accept => Accept(characterId),
            TradeAction.Decline => Decline(characterId),
            TradeAction.Add => Add(characterId, new InventoryLocation(container, slot), count),
            TradeAction.Remove => Remove(characterId, offerIndex),
            TradeAction.SetGold => SetGold(characterId, gold),
            TradeAction.Lock => Lock(characterId, revision),
            TradeAction.Confirm => Confirm(characterId, requestId, revision),
            TradeAction.Cancel => CancelBy(characterId),
            _ => TradeReason.TradeCancelled,
        };
        if (reason is { } answer)
            Answer(characterId, requestId, action, answer);
    }

    /// <summary><c>/trade &lt;name&gt;</c>: the world resolved the name to an entity (0: no such player in sight).</summary>
    public void InviteByName(long characterId, uint targetEntityId)
    {
        var reason = targetEntityId == 0 ? TradeReason.TradeTargetNotFound : Invite(characterId, targetEntityId) ?? TradeReason.None;
        Answer(characterId, 0, TradeAction.Invite, reason);
    }

    /// <summary>The player's connection ended: its trade is cancelled (unless the swap is already saving).</summary>
    public void Disconnected(long characterId)
    {
        if (_byCharacter.TryGetValue(characterId, out var trade) && trade.Phase != Phase.Settling)
            Cancel(trade, TradeEndReason.Disconnected, TradeReason.TradeCancelled);
    }

    /// <summary>A hot reload swapped the settings (restart class: only tests call this).</summary>
    public void Retune(TradeSettings settings) => _settings = settings;

    /// <summary>
    /// The character left the world (its last save in, or its session lost or faulted). An open trade is cancelled; during a
    /// swap the side is marked gone: the swap still finishes for the other side once its half committed (the pair commits as
    /// one), and the character is free to trade again after a new login.
    /// </summary>
    public void Detached(long characterId)
    {
        if (!_byCharacter.TryGetValue(characterId, out var trade))
            return;
        if (trade.Phase != Phase.Settling)
        {
            Cancel(trade, TradeEndReason.Disconnected, TradeReason.TradeCancelled);
            return;
        }
        (trade.A.CharacterId == characterId ? trade.A : trade.B).Gone = true;
        _byCharacter.Remove(characterId);
    }

    /// <summary>
    /// Once per tick, after the saves were read: answers swaps that committed, cancels trades whose players moved apart, died,
    /// entered combat, left or ran out of time, and resends the countdown while both sides are locked.
    /// </summary>
    public void Tick()
    {
        long now = _world.NowMs;
        SweepCooldowns(now);
        for (int i = 0; i < _trades.Count; i++)
        {
            var trade = _trades[i];
            if (trade.Phase == Phase.Settling)
            {
                CheckSettled(trade);
            }
            else if (CheckPair(trade.A.CharacterId, trade.B.CharacterId, out var end, out var reason) is false)
            {
                Cancel(trade, end, reason);
            }
            else if (now >= trade.DeadlineMs)
            {
                Cancel(trade, TradeEndReason.Timeout, TradeReason.TradeTimeout);
            }
            else if (trade.Phase == Phase.Open && trade.A.Locked && trade.B.Locked && now >= trade.NextResendMs)
            {
                SendStates(trade);
            }
            if (i < _trades.Count && _trades[i] != trade)
                i--; // removed
        }
    }

    private TradeReason? Invite(long characterId, uint targetEntityId)
    {
        if (!_world.TryGetEntity(targetEntityId, characterId, out var target))
            return TradeReason.TradeTargetNotFound;
        if (target.CharacterId == characterId)
            return TradeReason.TradeSelf;
        if (!_inventory.IsAttached(characterId) || !_inventory.HasGold(characterId)
            || !_inventory.IsAttached(target.CharacterId) || !_inventory.HasGold(target.CharacterId))
            return TradeReason.TradeUnavailable;
        if (_byCharacter.ContainsKey(characterId) || _byCharacter.ContainsKey(target.CharacterId))
            return TradeReason.TradeBusy;
        long now = _world.NowMs;
        if (InCooldown(characterId, now) || InCooldown(target.CharacterId, now))
            return TradeReason.TradeCooldown;
        if (CheckPair(characterId, target.CharacterId, out _, out var reason) is false)
            return reason;

        var trade = new Trade(++_nextId, new Side(characterId), new Side(target.CharacterId))
        {
            Phase = Phase.Invite,
            DeadlineMs = now + _settings.InviteTimeoutMs,
        };
        trade.A.ReservationKey = $"trade:{_runId}:{trade.Id}:{characterId}";
        trade.B.ReservationKey = $"trade:{_runId}:{trade.Id}:{target.CharacterId}";
        _trades.Add(trade);
        _byCharacter.Add(characterId, trade);
        _byCharacter.Add(target.CharacterId, trade);
        SendStates(trade);
        return TradeReason.None;
    }

    private TradeReason? Accept(long characterId)
    {
        if (!_byCharacter.TryGetValue(characterId, out var trade) || trade.Phase != Phase.Invite || trade.B.CharacterId != characterId)
            return TradeReason.TradeCancelled;
        if (CheckPair(trade.A.CharacterId, trade.B.CharacterId, out var end, out var reason) is false)
        {
            Cancel(trade, end, reason);
            return reason;
        }
        trade.Phase = Phase.Open;
        trade.DeadlineMs = _world.NowMs + _settings.LockTimeoutMs;
        SendStates(trade);
        return TradeReason.None;
    }

    private TradeReason? Decline(long characterId)
    {
        if (!_byCharacter.TryGetValue(characterId, out var trade) || trade.Phase != Phase.Invite || trade.B.CharacterId != characterId)
            return TradeReason.TradeCancelled;
        Cancel(trade, TradeEndReason.Declined, TradeReason.TradeDeclined);
        return TradeReason.None;
    }

    private TradeReason? CancelBy(long characterId)
    {
        if (!_byCharacter.TryGetValue(characterId, out var trade))
            return TradeReason.TradeCancelled;
        if (trade.Phase == Phase.Settling)
            return TradeReason.TradeBusy; // the swap is saving: too late to cancel
        Cancel(trade, TradeEndReason.Cancelled, TradeReason.TradeCancelled);
        return TradeReason.None;
    }

    private TradeReason? Add(long characterId, InventoryLocation at, int count)
    {
        if (!OpenSide(characterId, out var trade, out var side, out var refusal))
            return refusal;
        if (side.Offers.Count >= _settings.MaxSlots || side.Offers.Count >= ReliableMessages.MaxTradeOffers)
            return TradeReason.TradeSlotsFull;
        if (count <= 0)
            return TradeReason.TradeChanged;
        var items = _inventory.CurrentInventory(characterId);
        if (!items.TryIndex(at.Container, out int index) || at.Slot < 0 || at.Slot >= items.BagAt(index).SlotCount)
            return TradeReason.TradeChanged;
        var stack = items.BagAt(index)[at.Slot];
        if (stack.IsEmpty || stack.Count < count || (stack.IsInstance && count != 1))
            return TradeReason.TradeChanged;
        if (OfferCheck(characterId, stack, items.Rule(index).IsEquipped) is { } bad)
            return bad;

        var offer = OfferOf(characterId, at, stack, count);
        side.Offers.Add(offer);
        if (Reserve(characterId, side) is { } reserveRefusal)
        {
            side.Offers.RemoveAt(side.Offers.Count - 1);
            Reserve(characterId, side); // the previous set, which was held a moment ago
            return reserveRefusal;
        }
        Changed(trade);
        return TradeReason.None;
    }

    private TradeReason? Remove(long characterId, int offerIndex)
    {
        if (!OpenSide(characterId, out var trade, out var side, out var refusal))
            return refusal;
        if (offerIndex < 0 || offerIndex >= side.Offers.Count)
            return TradeReason.TradeChanged;
        side.Offers.RemoveAt(offerIndex);
        Reserve(characterId, side);
        Changed(trade);
        return TradeReason.None;
    }

    private TradeReason? SetGold(long characterId, long gold)
    {
        if (!OpenSide(characterId, out var trade, out var side, out var refusal))
            return refusal;
        if (gold < 0 || gold > _settings.MaxGoldPerTrade)
            return TradeReason.TradeGoldCap;
        if (gold > _inventory.Gold(characterId))
            return TradeReason.TradeNotEnoughGold;
        if (side.Gold == gold)
            return TradeReason.None;
        side.Gold = gold;
        Changed(trade);
        return TradeReason.None;
    }

    private TradeReason? Lock(long characterId, uint revision)
    {
        if (!OpenSide(characterId, out var trade, out var side, out var refusal))
            return refusal;
        if (revision != trade.Revision)
            return TradeReason.TradeChanged;
        if (side.Locked)
            return TradeReason.None;
        side.Locked = true;
        if (trade.A.Locked && trade.B.Locked)
            trade.DeadlineMs = _world.NowMs + _settings.ConfirmTimeoutMs; // both lock, then both confirm within this
        SendStates(trade);
        return TradeReason.None;
    }

    /// <summary>A confirm is answered when the swap committed, or with the reason it did not happen; null: held.</summary>
    private TradeReason? Confirm(long characterId, uint requestId, uint revision)
    {
        if (!OpenSide(characterId, out var trade, out var side, out var refusal))
            return refusal;
        if (revision != trade.Revision || !trade.A.Locked || !trade.B.Locked)
            return TradeReason.TradeChanged;
        if (side.Confirmed)
            return TradeReason.None;
        side.Confirmed = true;
        side.ConfirmRequestId = requestId;
        if (!trade.A.Confirmed || !trade.B.Confirmed)
        {
            SendStates(trade);
            return null;
        }
        Settle(trade);
        return null;
    }

    /// <summary>Both confirmed: re-validate every unit and the gold, swap in memory, save both in one transaction.</summary>
    private void Settle(Trade trade)
    {
        if (CheckPair(trade.A.CharacterId, trade.B.CharacterId, out var end, out var pairReason) is false)
        {
            Cancel(trade, end, pairReason);
            return;
        }
        // Every unit again (the flags of R-4.1-04 and R-4.5-01 can have changed, e.g. a stolen mark, M5), then the gold.
        var reason = Revalidate(trade.A) ?? Revalidate(trade.B);
        if (reason == null && (trade.A.Gold > _inventory.Gold(trade.A.CharacterId) || trade.B.Gold > _inventory.Gold(trade.B.CharacterId)))
            reason = TradeReason.TradeChanged;
        if (reason == null)
            reason = Reason(_inventory.CheckTransfer(SideOf(trade.A), SideOf(trade.B)));
        if (reason == null)
        {
            // Link the two saves first (the step that can refuse), then swap: once linked neither character is saved alone,
            // and the swap applies the plan just checked on the same state, so nothing is left half done.
            string opKey = $"trade:{_runId}:{trade.Id}";
            var record = TransferRecord.Create(opKey, TransferKind.Trade, trade.A.CharacterId, trade.B.CharacterId, Summary(trade));
            if (!_world.RequestTransferSave(record))
            {
                reason = TradeReason.TradeUnavailable;
            }
            else
            {
                reason = Reason(_inventory.TryTransfer(SideOf(trade.A), SideOf(trade.B), opKey, InventoryReason.Trade,
                    _settings.OwnerHistoryMax, out var keys));
                if (reason == null)
                {
                    trade.OpKey = opKey;
                    trade.Keys = keys;
                    trade.Phase = Phase.Settling;
                    SendStates(trade);
                    return;
                }
                _world.AbortTransferSave(record); // nothing was applied: the two characters save alone again
            }
        }
        // Refused: nothing moved. The window stays open; both look again, lock and confirm (TradeChanged and friends).
        AnswerConfirms(trade, reason!.Value);
        Changed(trade);
    }

    private static TradeReason? Reason(TransferRefusal refusal) => refusal switch
    {
        TransferRefusal.None => null,
        TransferRefusal.GoldShort => TradeReason.TradeNotEnoughGold,
        TransferRefusal.GoldCap => TradeReason.TradeGoldCap,
        TransferRefusal.BagFull => TradeReason.TradeBagFull,
        TransferRefusal.Empty => TradeReason.TradeEmpty,
        _ => TradeReason.TradeChanged,
    };

    /// <summary>
    /// A swap is saving. Each side still in the world waits for its own half to commit; a side that left (its session lost
    /// after the pair job was sent) does not hold the other: the pair commits or fails as one, so once every remaining side
    /// committed the trade is done for them. When the pair can no longer be written (a side dropped before the send faults its
    /// partner, a failed pair faults both), no side is left and the trade is dropped.
    /// </summary>
    private void CheckSettled(Trade trade)
    {
        if (trade.A.Gone && trade.B.Gone)
        {
            _inventory.ForgetTransfer(trade.OpKey!);
            Remove(trade);
            return;
        }
        foreach (var side in trade.Sides)
        {
            if (!side.Gone && !_inventory.IsCommitted(side.CharacterId, side == trade.A ? trade.Keys.KeyA : trade.Keys.KeyB))
                return;
        }
        long a = trade.A.CharacterId, b = trade.B.CharacterId;
        _events?.Publish(new TradeCompleted(a, b, Traded(trade.A), Traded(trade.B), trade.A.Gold, trade.B.Gold, trade.OpKey!), a);
        trade.Phase = Phase.Done;
        AnswerConfirms(trade, TradeReason.None);
        SendStates(trade);
        _inventory.ForgetTransfer(trade.OpKey!);
        Remove(trade);
    }

    /// <summary>False with the end and reason when the two players can no longer trade (range, alive, combat, connection).</summary>
    private bool CheckPair(long a, long b, out TradeEndReason end, out TradeReason reason)
    {
        end = TradeEndReason.Cancelled;
        reason = TradeReason.None;
        if (!_world.TryGetCharacter(a, out var pa) || !_world.TryGetCharacter(b, out var pb) || !pa.Connected || !pb.Connected)
        {
            (end, reason) = (TradeEndReason.Disconnected, TradeReason.TradeCancelled);
            return false;
        }
        if (!pa.Alive || !pb.Alive)
        {
            (end, reason) = (TradeEndReason.Dead, TradeReason.TradeDead);
            return false;
        }
        if (pa.InCombat || pb.InCombat)
        {
            (end, reason) = (TradeEndReason.InCombat, TradeReason.TradeInCombat);
            return false;
        }
        long dx = pa.XCm - pb.XCm, dy = pa.YCm - pb.YCm;
        if (dx * dx + dy * dy > (long)_settings.RangeCm * _settings.RangeCm)
        {
            (end, reason) = (TradeEndReason.TooFar, TradeReason.TradeTooFar);
            return false;
        }
        return true;
    }

    private TradeReason? OfferCheck(long characterId, in ItemStack stack, bool inEquipped)
    {
        if (!_inventory.Rules.TryGetItem(stack.ItemNetId, out var def))
            return TradeReason.TradeChanged;
        bool tainted = stack.IsInstance && _inventory.TryGetLatestProperties(characterId, stack.InstanceId, out var properties)
                       && properties.Tainted;
        return TradeRules.CheckOffer(def.Category, stack.Stolen, tainted, _inventory.IsNonTradable(characterId, stack), inEquipped) switch
        {
            TradeOfferCheck.Ok => null,
            TradeOfferCheck.SellOnly => TradeReason.TradeSellOnly,
            TradeOfferCheck.Quest => TradeReason.TradeQuestItem,
            TradeOfferCheck.Stolen => TradeReason.TradeStolen,
            TradeOfferCheck.NotTradable => TradeReason.TradeNotTradable,
            _ => TradeReason.TradeEquipped,
        };
    }

    /// <summary>Each offered unit still lies where it was offered, under this trade's reservation, and passes the checks.</summary>
    private TradeReason? Revalidate(Side side)
    {
        if (side.Offers.Count == 0)
            return null;
        if (!_inventory.HasReservation(side.CharacterId, side.ReservationKey!))
            return TradeReason.TradeChanged;
        var items = _inventory.CurrentInventory(side.CharacterId);
        foreach (var offer in side.Offers)
        {
            if (!items.TryIndex(offer.At.Container, out int index))
                return TradeReason.TradeChanged;
            var stack = items.BagAt(index)[offer.At.Slot];
            if (stack.ItemNetId != offer.ItemNetId || stack.InstanceId != offer.InstanceId || stack.Count < offer.Count)
                return TradeReason.TradeChanged;
            if (OfferCheck(side.CharacterId, stack, items.Rule(index).IsEquipped) is { } bad)
                return bad;
        }
        return null;
    }

    /// <summary>Reserves the side's whole offer set under its key (all or nothing); null when held.</summary>
    private TradeReason? Reserve(long characterId, Side side)
    {
        _inventory.Release(characterId, side.ReservationKey!);
        if (side.Offers.Count == 0)
            return null;
        var items = new List<ReserveItem>(side.Offers.Count);
        foreach (var offer in side.Offers)
            items.Add(new ReserveItem(offer.At, offer.Count));
        return _inventory.Reserve(characterId, side.ReservationKey!, items) switch
        {
            ReserveResult.Ok => null,
            ReserveResult.ItemReserved => TradeReason.TradeReserved,
            _ => TradeReason.TradeChanged,
        };
    }

    private Offer OfferOf(long characterId, InventoryLocation at, in ItemStack stack, int count)
    {
        byte enchant = 0, gems = 0;
        ushort quality = 0;
        if (stack.IsInstance && _inventory.TryGetLatestProperties(characterId, stack.InstanceId, out var properties))
        {
            enchant = (byte)Math.Clamp(properties.Enchant, 0, byte.MaxValue);
            quality = (ushort)Math.Clamp(properties.Quality, 0, ushort.MaxValue);
            gems = (byte)Math.Clamp(properties.Gems.Count, 0, byte.MaxValue);
        }
        return new Offer(at, count, stack.ItemNetId, stack.Rarity, stack.InstanceId, enchant, quality, gems);
    }

    private bool OpenSide(long characterId, out Trade trade, out Side side, out TradeReason? refusal)
    {
        side = null!;
        refusal = null;
        if (!_byCharacter.TryGetValue(characterId, out trade!) || trade.Phase != Phase.Open)
        {
            refusal = trade?.Phase == Phase.Settling ? TradeReason.TradeBusy : TradeReason.TradeCancelled;
            return false;
        }
        side = trade.A.CharacterId == characterId ? trade.A : trade.B;
        return true;
    }

    /// <summary>An offer changed: a new revision, both locks and confirms cleared (decision 5), the deadline back to the lock time.</summary>
    private void Changed(Trade trade)
    {
        AnswerConfirms(trade, TradeReason.TradeChanged); // a held confirm is void once the offers change
        trade.Revision++;
        bool wasLocked = trade.A.Locked && trade.B.Locked;
        foreach (var side in trade.Sides)
        {
            side.Locked = false;
            side.Confirmed = false;
        }
        if (wasLocked)
            trade.DeadlineMs = _world.NowMs + _settings.LockTimeoutMs;
        SendStates(trade);
    }

    private void Cancel(Trade trade, TradeEndReason end, TradeReason reason)
    {
        foreach (var side in trade.Sides)
            _inventory.Release(side.CharacterId, side.ReservationKey!);
        long until = _world.NowMs + _settings.CancelCooldownMs;
        _cooldownUntil[trade.A.CharacterId] = until;
        _cooldownUntil[trade.B.CharacterId] = until;
        trade.Phase = Phase.Cancelled;
        trade.EndReason = reason;
        AnswerConfirms(trade, reason);
        SendStates(trade);
        _events?.Publish(new TradeCancelled(trade.A.CharacterId, trade.B.CharacterId, end), trade.A.CharacterId);
        Remove(trade);
    }

    private void Remove(Trade trade)
    {
        _trades.Remove(trade);
        foreach (var side in trade.Sides)
        {
            if (_byCharacter.TryGetValue(side.CharacterId, out var own) && own == trade)
                _byCharacter.Remove(side.CharacterId);
        }
    }

    /// <summary>Drops the cooldowns that ran out, so the map holds only players who cancelled within the cooldown.</summary>
    private void SweepCooldowns(long now)
    {
        if (_cooldownUntil.Count == 0)
            return;
        _expired.Clear();
        foreach (var (characterId, until) in _cooldownUntil)
        {
            if (now >= until)
                _expired.Add(characterId);
        }
        foreach (long characterId in _expired)
            _cooldownUntil.Remove(characterId);
    }

    private bool InCooldown(long characterId, long now)
    {
        if (!_cooldownUntil.TryGetValue(characterId, out long until))
            return false;
        if (now < until)
            return true;
        _cooldownUntil.Remove(characterId);
        return false;
    }

    private void AnswerConfirms(Trade trade, TradeReason reason)
    {
        foreach (var side in trade.Sides)
        {
            if (side.ConfirmRequestId is not { } requestId)
                continue;
            Answer(side.CharacterId, requestId, TradeAction.Confirm, reason);
            side.ConfirmRequestId = null;
        }
    }

    private void Answer(long characterId, uint requestId, TradeAction action, TradeReason reason) =>
        _outbox.Add(new TradeMessage(characterId, new TradeResult { RequestId = requestId, Action = action, Reason = reason }, null));

    private void SendStates(Trade trade)
    {
        long now = _world.NowMs;
        trade.NextResendMs = now + _settings.StateResendMs;
        foreach (var side in trade.Sides)
        {
            var other = side == trade.A ? trade.B : trade.A;
            _world.TryGetCharacter(other.CharacterId, out var partner);
            var phase = trade.Phase switch
            {
                Phase.Invite => side == trade.A ? TradePhase.Inviting : TradePhase.Invited,
                Phase.Open => TradePhase.Open,
                Phase.Settling => TradePhase.Settling,
                Phase.Done => TradePhase.Done,
                _ => TradePhase.Cancelled,
            };
            _outbox.Add(new TradeMessage(side.CharacterId, null, new TradeState
            {
                TradeId = trade.Id,
                Revision = trade.Revision,
                Phase = phase,
                PartnerEntityId = partner.EntityId,
                PartnerName = _world.NameFor(other.CharacterId, side.CharacterId),
                OwnOffer = Wire(side, own: true),
                PartnerOffer = Wire(other, own: false),
                OwnGold = side.Gold,
                PartnerGold = other.Gold,
                OwnLocked = side.Locked,
                PartnerLocked = other.Locked,
                OwnConfirmed = side.Confirmed,
                PartnerConfirmed = other.Confirmed,
                MsLeft = phase is TradePhase.Inviting or TradePhase.Invited or TradePhase.Open
                    ? (int)Math.Clamp(trade.DeadlineMs - now, 0, int.MaxValue)
                    : 0,
                Reason = phase == TradePhase.Cancelled ? trade.EndReason : TradeReason.None,
            }));
        }
    }

    private static TradeOffer[] Wire(Side side, bool own)
    {
        if (side.Offers.Count == 0)
            return Array.Empty<TradeOffer>();
        var offers = new TradeOffer[side.Offers.Count];
        for (int i = 0; i < offers.Length; i++)
        {
            var offer = side.Offers[i];
            offers[i] = new TradeOffer
            {
                ItemNetId = offer.ItemNetId, Rarity = offer.Rarity, Count = (ushort)offer.Count,
                Container = own ? offer.At.Container : (byte)0, Slot = own ? (ushort)offer.At.Slot : (ushort)0,
                Enchant = offer.Enchant, Quality = offer.Quality, GemCount = offer.GemCount,
            };
        }
        return offers;
    }

    private static TransferSide SideOf(Side side) =>
        new(side.CharacterId, side.Offers.Count > 0 ? side.ReservationKey : null, side.Gold);

    private static IReadOnlyList<TradedItem> Traded(Side side) =>
        side.Offers.Select(o => new TradedItem(o.ItemNetId, o.Rarity, o.Count, o.InstanceId)).ToList();

    /// <summary>The <c>transfer_ops.summary</c> JSON: what moved each way (a = the lower character id).</summary>
    private static string Summary(Trade trade)
    {
        var (low, high) = trade.A.CharacterId < trade.B.CharacterId ? (trade.A, trade.B) : (trade.B, trade.A);
        static string Items(Side side) => "[" + string.Join(",", side.Offers.Select(o =>
            $"{{\"item\":{o.ItemNetId},\"rarity\":{o.Rarity},\"count\":{o.Count},\"instance\":{o.InstanceId}}}")) + "]";
        return $"{{\"aToB\":{{\"items\":{Items(low)},\"gold\":{low.Gold}}},\"bToA\":{{\"items\":{Items(high)},\"gold\":{high.Gold}}}}}";
    }

    private enum Phase : byte
    {
        Invite,
        Open,
        Settling,
        Done,
        Cancelled,
    }

    private readonly record struct Offer(InventoryLocation At, int Count, ushort ItemNetId, byte Rarity, long InstanceId, byte Enchant,
        ushort Quality, byte GemCount);

    private sealed class Side(long characterId)
    {
        public long CharacterId { get; } = characterId;
        public string? ReservationKey { get; set; }
        public List<Offer> Offers { get; } = new();
        public long Gold { get; set; }
        public bool Locked { get; set; }
        public bool Confirmed { get; set; }
        /// <summary>The held confirm request, answered at the commit or with the reason the swap did not happen.</summary>
        public uint? ConfirmRequestId { get; set; }

        /// <summary>Left the world while the swap was saving (<see cref="Detached"/>).</summary>
        public bool Gone { get; set; }
    }

    private sealed class Trade(uint id, Side a, Side b)
    {
        public uint Id { get; } = id;

        /// <summary>The inviter.</summary>
        public Side A { get; } = a;

        public Side B { get; } = b;

        public Side[] Sides { get; } = { a, b };
        public Phase Phase { get; set; }
        public uint Revision { get; set; }
        public long DeadlineMs { get; set; }
        public long NextResendMs { get; set; }
        public string? OpKey { get; set; }
        public TransferKeys Keys { get; set; }
        public TradeReason EndReason { get; set; }
    }
}
