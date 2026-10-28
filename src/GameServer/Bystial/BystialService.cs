using GameServer.Combat;
using GameServer.Entities;
using GameServer.Events;
using GameServer.Persistence;
using GameServer.Progression;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Bystial;
using Shared.Simulation.Stats;

namespace GameServer.Bystial;

/// <summary>What a book change request did (<see cref="BystialService.TryChange"/>).</summary>
public readonly record struct BookChangeOutcome(BookReason Reason, bool Repeated)
{
    public bool Applied => Reason == BookReason.None;
}

/// <summary>
/// The Bystial book of every character held by this world (spec 14.2, WP-M2-8), the only writer of book state (rule 4),
/// in memory on the world thread.
/// <list type="bullet">
/// <item><b>Cards (R-14.2-02).</b> On <see cref="Killed"/> (phase 11, its own <see cref="EventWatermark"/>, subscribed after
/// <c>AchievementService</c> so the count includes the kill) it reads the kill count of the monster's type from the
/// <see cref="ProgressionStore"/> (R-14.2-01; it never counts kills itself) and raises the card to the tier that count
/// reaches (<see cref="BystialMath.TierFor"/>). A tier never goes down. Each tier step is one operation with the key
/// <c>bystial:{character}:{monster}:{tier}</c>, unique for ever, so a replayed kill or a retried save cannot earn a card
/// twice. <see cref="CatchUp"/> runs the same check on the loaded counts at login (kills from before the book, or lowered thresholds).</item>
/// <item><b>Place, swap, take out (R-14.2-04/06).</b> <see cref="TryChange"/> under the request's operation key; the
/// combat check is the caller's (<see cref="BookChangeHandler"/>).</item>
/// <item><b>Saving</b> (R-7.1-01 class 2): a change marks the character class 2 dirty; the next character save takes the
/// batch (<see cref="IBystialBatchSource"/>): the changed cards as absolute rows and the operations with their audit rows,
/// in the save's transaction. A crash loses at most the last batch, never duplicates (R-7.1-02).</item>
/// <item><b>Effects (R-14.2-05)</b> come from the committed book only (rule 5): on a commit the bonuses are aggregated
/// again (<see cref="BystialMath.Aggregate"/>, family set bonuses included) and <see cref="CardEarned"/>,
/// <see cref="CardEvolved"/> and <see cref="BookChanged"/> are published. Stats read <see cref="BonusesOf"/>; hits read
/// <see cref="ICardCombatBonus"/>; exp reads <see cref="ExpPercent"/>. TODO(M5): the arena uses class templates (spec 6.6)
/// and never applies card bonuses (user decision 2026-10-27); its stat blocks must not take the <c>bystialCard</c> source.</item>
/// </list>
/// </summary>
public sealed class BystialService : IBystialBatchSource, IEventConsumer<Killed>, IEventConsumer<RankChanged>, ICardCombatBonus
{
    private readonly BystialRules _rules;
    private readonly ProgressionStore _progression;
    private readonly Func<CombatRules?> _combat;
    private readonly EventBus? _events;
    private readonly Dictionary<long, CharacterBook> _books = new();
    private readonly List<PlacedCard> _placed = new();
    private EventWatermark _watermark;
    private EventWatermark _rankWatermark;

    public BystialService(BystialRules rules, ProgressionStore progression, Func<CombatRules?> combat, EventBus? events)
    {
        _rules = rules;
        _progression = progression;
        _combat = combat;
        _events = events;
    }

    public BystialRules Rules => _rules;

    /// <summary>Marks a character class 2 dirty with its save scheduler (set by the SessionManager).</summary>
    public Action<long>? MarkClass2 { get; set; }

    /// <summary>The character's book as its client sees it changed (set by the world): send a <see cref="BookState"/>.</summary>
    public Action<long, BookCause>? Changed { get; set; }

    /// <summary>The player entity id of a character, 0 when it has none in this world (set by the world, for the events).</summary>
    public Func<long, uint>? EntityOf { get; set; }

    public int Count => _books.Count;

    public bool IsAttached(long characterId) => _books.ContainsKey(characterId);

    public void Subscribe(EventBus events)
    {
        events.Subscribe<Killed>(this);
        events.Subscribe<RankChanged>(this);
    }

    /// <summary>
    /// Starts holding a character's book as loaded at login. Cards of monsters the tables no longer card stay stored but give
    /// nothing. <see cref="CatchUp"/> then raises the cards to the tiers the loaded kill counts reach.
    /// </summary>
    public void Attach(long characterId, BystialRecord record)
    {
        if (_books.ContainsKey(characterId))
            throw new InvalidOperationException($"character {characterId} is already attached");
        var book = new CharacterBook();
        foreach (var row in record.Cards)
        {
            if (row.Tier == BystialTier.None)
                continue;
            var card = new Card(row.Tier, row.Slot ?? -1);
            book.Cards[row.MonsterDefId] = card;
            book.Committed[row.MonsterDefId] = card;
        }
        _books.Add(characterId, book);
        book.Bonuses = Aggregate(book.Committed);
    }

    /// <summary>
    /// Raises a just attached character's cards to the tiers its loaded kill counts reach: kills from before the book, or
    /// thresholds lowered since (the progression must be attached). Called once the save scheduler tracks the character, so
    /// the steps ride on its next class 2 save. Idempotent: a tier it holds is never stepped again.
    /// </summary>
    public void CatchUp(long characterId)
    {
        if (!_books.TryGetValue(characterId, out var book) || !_progression.IsAttached(characterId))
            return;
        foreach (var ladder in _rules.Ladders) // monster id order (rule 6)
            Evolve(characterId, book, ladder, _progression.KillCount(characterId, ladder.MonsterId));
    }

    /// <summary>Drops a character (it left the world). Unsaved changes of a faulted character are lost with it.</summary>
    public void Detach(long characterId) => _books.Remove(characterId);

    public void Handle(in EventEnvelope envelope, in Killed payload)
    {
        if (!_watermark.TryAccept(envelope))
            return; // a re-dispatched kill evolves nothing twice
        long characterId = envelope.ActorCharacterId;
        if (characterId == 0 || payload.VictimCharacterId != 0 || !_books.TryGetValue(characterId, out var book)
            || !_progression.IsAttached(characterId))
            return;
        if (_combat() is not { } combat || !combat.TryGetMonster(payload.VictimDefId, out var monster)
            || !_rules.TryGetLadder(monster.Id, out var ladder))
            return;
        Changed?.Invoke(characterId, BookCause.Kills); // the counter toward the next tier moved
        Evolve(characterId, book, ladder, _progression.KillCount(characterId, monster.Id));
    }

    /// <summary>A rank-up committed: more slots may be open (R-14.2-04); the client hears of it.</summary>
    public void Handle(in EventEnvelope envelope, in RankChanged payload)
    {
        if (_rankWatermark.TryAccept(envelope) && _books.ContainsKey(payload.CharacterId))
            Changed?.Invoke(payload.CharacterId, BookCause.Changed);
    }

    /// <summary>The card of <paramref name="ladder"/> at the tier <paramref name="kills"/> reaches, when that is higher than it holds.</summary>
    private void Evolve(long characterId, CharacterBook book, CardLadder ladder, long kills)
    {
        var tier = BystialMath.TierFor(kills, ladder.Thresholds);
        var from = book.Cards.TryGetValue(ladder.MonsterId, out var card) ? card.Tier : BystialTier.None;
        if (tier <= from)
            return; // never down (R-14.2-03: the card is the character's)
        int slot = card?.Slot ?? -1;
        book.Cards[ladder.MonsterId] = new Card(tier, slot);
        book.DirtyCards.Add(ladder.MonsterId);
        book.PendingOps.Add(new BystialOpRecord(TierOpKey(characterId, ladder.MonsterId, tier),
            from == BystialTier.None ? BystialOpKind.Earn : BystialOpKind.Evolve, ladder.MonsterId, null, from, tier,
            slot >= 0 ? slot : null, kills));
        MarkClass2?.Invoke(characterId);
    }

    /// <summary>The operation key of a tier step: one per character, monster type and tier, ever.</summary>
    public static string TierOpKey(long characterId, string monsterId, BystialTier tier) =>
        $"bystial:{characterId}:{monsterId}:{(int)tier}";

    /// <summary>
    /// Places the card of <paramref name="monsterNetId"/> into <paramref name="slot"/> (an occupied slot swaps: its card goes
    /// back to the collection), into the first free slot (<see cref="BookLimits.AnyFreeSlot"/>), or takes it out
    /// (<see cref="BookLimits.NoSlot"/>), under <paramref name="opKey"/>; the slots open are those of <paramref name="rank"/>
    /// (<c>BystialSlots</c>). A repeat of an applied key returns its first answer and changes nothing (rule 5). A refusal
    /// changes and records nothing. Reasons in order: <see cref="BookReason.CardNotEarned"/>,
    /// <see cref="BookReason.CardNotPlaced"/> (take out), <see cref="BookReason.AlreadyPlaced"/>,
    /// <see cref="BookReason.NoFreeBookSlot"/>.
    /// </summary>
    public BookChangeOutcome TryChange(long characterId, int monsterNetId, byte slot, Rank rank, string opKey)
    {
        if (!_books.TryGetValue(characterId, out var book))
            return new BookChangeOutcome(BookReason.BookUnavailable, false);
        if (book.SeenOps.Contains(opKey))
            return new BookChangeOutcome(BookReason.None, true);
        if (!_rules.TryGetLadder(monsterNetId, out var ladder) || !book.Cards.TryGetValue(ladder.MonsterId, out var card))
            return new BookChangeOutcome(BookReason.CardNotEarned, false);

        if (slot == BookLimits.NoSlot)
        {
            if (card.Slot < 0)
                return new BookChangeOutcome(BookReason.CardNotPlaced, false);
            Apply(characterId, book, opKey, BystialOpKind.TakeOut, ladder.MonsterId, card, null, card.Slot, -1);
            return new BookChangeOutcome(BookReason.None, false);
        }
        if (card.Slot >= 0)
            return new BookChangeOutcome(BookReason.AlreadyPlaced, false);
        int open = _rules.SlotsFor(rank);
        int target = slot == BookLimits.AnyFreeSlot ? FirstFree(book, open) : slot;
        if (target < 0 || target >= open)
            return new BookChangeOutcome(BookReason.NoFreeBookSlot, false);
        string? occupant = Occupant(book, target);
        if (occupant != null)
        {
            var other = book.Cards[occupant];
            book.Cards[occupant] = new Card(other.Tier, -1);
            book.DirtyCards.Add(occupant);
        }
        Apply(characterId, book, opKey, occupant == null ? BystialOpKind.Place : BystialOpKind.Swap, ladder.MonsterId, card, occupant,
            target, target);
        return new BookChangeOutcome(BookReason.None, false);
    }

    private void Apply(long characterId, CharacterBook book, string opKey, BystialOpKind kind, string monsterId, Card card,
        string? other, int auditSlot, int newSlot)
    {
        book.Cards[monsterId] = new Card(card.Tier, newSlot);
        book.DirtyCards.Add(monsterId);
        book.PendingOps.Add(new BystialOpRecord(opKey, kind, monsterId, other, card.Tier, card.Tier, auditSlot, null));
        book.SeenOps.Add(opKey);
        MarkClass2?.Invoke(characterId);
    }

    private static int FirstFree(CharacterBook book, int open)
    {
        for (int slot = 0; slot < open; slot++)
        {
            if (Occupant(book, slot) == null)
                return slot;
        }
        return -1;
    }

    private static string? Occupant(CharacterBook book, int slot)
    {
        foreach (var (monsterId, card) in book.Cards)
        {
            if (card.Slot == slot)
                return monsterId;
        }
        return null;
    }

    /// <summary>The operation <paramref name="opKey"/> was applied (committed or not) since the character was attached.</summary>
    public bool WasApplied(long characterId, string opKey) =>
        _books.TryGetValue(characterId, out var book) && book.SeenOps.Contains(opKey);

    /// <summary>True once <paramref name="opKey"/> was applied and is in a committed save (rule 5: then it may be confirmed).</summary>
    public bool IsCommitted(long characterId, string opKey)
    {
        if (!_books.TryGetValue(characterId, out var book) || !book.SeenOps.Contains(opKey))
            return false;
        foreach (var op in book.PendingOps)
        {
            if (op.OpKey == opKey)
                return false;
        }
        if (book.InFlight is { } inFlight)
        {
            foreach (var op in inFlight.Batch.Ops)
            {
                if (op.OpKey == opKey)
                    return false;
            }
        }
        return true;
    }

    public BystialBatch? TakeBatch(long characterId, Guid saveId)
    {
        if (!_books.TryGetValue(characterId, out var book) || (book.DirtyCards.Count == 0 && book.PendingOps.Count == 0))
            return null;
        if (book.InFlight != null)
            throw new InvalidOperationException($"character {characterId} already has a book batch in flight");
        var cards = new List<BystialCardRow>(book.DirtyCards.Count);
        foreach (var id in book.DirtyCards) // sorted: rows in a fixed order (rule 6)
        {
            var card = book.Cards[id];
            cards.Add(new BystialCardRow(id, card.Tier, card.Slot >= 0 ? card.Slot : null));
        }
        var batch = new BystialBatch(cards, book.PendingOps.ToList());
        book.InFlight = new InFlightBatch(saveId, batch, new SortedDictionary<string, Card>(book.Cards, StringComparer.Ordinal));
        book.DirtyCards.Clear();
        book.PendingOps.Clear();
        return batch;
    }

    public void Committed(long characterId, Guid saveId)
    {
        if (!_books.TryGetValue(characterId, out var book) || book.InFlight is not { } inFlight || inFlight.SaveId != saveId)
            return; // a save without a batch, or a stale completion: idempotent
        book.InFlight = null;
        bool placementChanged = !SamePlacement(book.Committed, inFlight.Snapshot);
        book.Committed = inFlight.Snapshot;
        uint entityId = EntityOf?.Invoke(characterId) ?? 0;
        var cause = BookCause.Changed;
        foreach (var op in inFlight.Batch.Ops)
        {
            if (!_rules.TryGetLadder(op.MonsterDefId, out var ladder))
                continue;
            long kills = op.KillCount ?? 0;
            if (op.Kind == BystialOpKind.Earn)
            {
                _events?.Publish(new CardEarned(characterId, entityId, ladder.NetId, op.ToTier, kills), characterId);
                cause = BookCause.CardEarned;
            }
            else if (op.Kind == BystialOpKind.Evolve)
            {
                _events?.Publish(new CardEvolved(characterId, entityId, ladder.NetId, op.FromTier, op.ToTier, kills), characterId);
                if (cause != BookCause.CardEarned)
                    cause = BookCause.CardEvolved;
            }
        }
        if (placementChanged)
        {
            book.Bonuses = Aggregate(book.Committed);
            book.Version++;
            _events?.Publish(new BookChanged(characterId, entityId, book.Version, PlacedCount(book.Committed)), characterId);
        }
        Changed?.Invoke(characterId, cause);
    }

    /// <summary>The same cards in the same slots at the same tiers: what the book gives is unchanged.</summary>
    private static bool SamePlacement(SortedDictionary<string, Card> before, SortedDictionary<string, Card> after)
    {
        foreach (var (id, card) in after)
        {
            var was = before.TryGetValue(id, out var old) ? old : null;
            if (card.Slot >= 0 ? was is null || was.Slot != card.Slot || was.Tier != card.Tier : was is { Slot: >= 0 })
                return false;
        }
        foreach (var (id, card) in before)
        {
            if (card.Slot >= 0 && !after.ContainsKey(id))
                return false;
        }
        return true;
    }

    private static int PlacedCount(SortedDictionary<string, Card> cards)
    {
        int placed = 0;
        foreach (var card in cards.Values)
        {
            if (card.Slot >= 0)
                placed++;
        }
        return placed;
    }

    private BookBonuses Aggregate(SortedDictionary<string, Card> cards)
    {
        _placed.Clear();
        foreach (var (id, card) in cards) // monster id order (rule 6)
        {
            if (card.Slot >= 0 && _rules.TryGetLadder(id, out var ladder) && card.Tier <= ladder.TopTier)
                _placed.Add(new PlacedCard(ladder.Family, ladder.BonusAt(card.Tier)));
        }
        return BystialMath.Aggregate(_placed, _rules.SetBonuses, _rules.Caps);
    }

    /// <summary>What the committed book gives (stats, damage and exp against families); empty for a character not held.</summary>
    public BookBonuses BonusesOf(long characterId) =>
        _books.TryGetValue(characterId, out var book) ? book.Bonuses : BookBonuses.Empty;

    /// <summary>The <see cref="BookChanged"/> version of the committed book.</summary>
    public int VersionOf(long characterId) => _books.TryGetValue(characterId, out var book) ? book.Version : 0;

    /// <summary>Whole percents more damage the attacker's book gives against the target monster's family (direct hits and projectiles).</summary>
    public int DamagePercent(Entity attacker, Entity target) =>
        attacker is Player player && target is Monster monster ? BonusesOf(player.CharacterId).DamagePercent(monster.Def.Family) : 0;

    /// <summary>Whole percents more exp the character's book gives from kills of <paramref name="monster"/>'s family.</summary>
    public int ExpPercent(long characterId, MonsterDef monster) => BonusesOf(characterId).ExpPercent(monster.Family);

    /// <summary>The cards as the client sees them: committed tier and slot (rule 5), by monster id.</summary>
    public IReadOnlyDictionary<string, (BystialTier Tier, int Slot)> CommittedCards(long characterId)
    {
        var result = new SortedDictionary<string, (BystialTier, int)>(StringComparer.Ordinal);
        if (_books.TryGetValue(characterId, out var book))
        {
            foreach (var (id, card) in book.Committed)
                result[id] = (card.Tier, card.Slot);
        }
        return result;
    }

    /// <summary>The card of a monster type now (pending changes included); tier none and slot -1 without one. Tests and tools.</summary>
    public (BystialTier Tier, int Slot) CardOf(long characterId, string monsterId) =>
        _books.TryGetValue(characterId, out var book) && book.Cards.TryGetValue(monsterId, out var card) ? (card.Tier, card.Slot) : (BystialTier.None, -1);

    /// <summary>The <see cref="BookState"/> for a client: the committed cards, the live counters, the slots open at the rank.</summary>
    internal BookState StateFor(long characterId, Rank rank, uint version, BookCause cause)
    {
        _books.TryGetValue(characterId, out var book);
        var cards = new List<BookCard>();
        foreach (var ladder in _rules.Ladders) // monster id order (rule 6)
        {
            long kills = _progression.IsAttached(characterId) ? _progression.KillCount(characterId, ladder.MonsterId) : 0;
            Card? card = null;
            book?.Committed.TryGetValue(ladder.MonsterId, out card);
            var tier = card?.Tier ?? BystialTier.None;
            if (kills == 0 && tier == BystialTier.None)
                continue;
            cards.Add(new BookCard
            {
                MonsterNetId = ladder.NetId,
                Tier = tier,
                Slot = card is { Slot: >= 0 } ? (byte)card.Slot : BookLimits.NoSlot,
                Kills = kills,
                NextTierKills = BystialMath.NextTierKills(tier, ladder.Thresholds),
            });
            if (cards.Count == BookLimits.MaxCards)
                break;
        }
        return new BookState
        {
            Version = version,
            Cause = cause,
            SlotCount = (byte)Math.Min(BookLimits.MaxSlots, _rules.SlotsFor(rank)),
            Cards = cards.ToArray(),
        };
    }

    private sealed record Card(BystialTier Tier, int Slot);

    private sealed record InFlightBatch(Guid SaveId, BystialBatch Batch, SortedDictionary<string, Card> Snapshot);

    private sealed class CharacterBook
    {
        /// <summary>The cards now, pending changes included, by monster id (ordinal order).</summary>
        public SortedDictionary<string, Card> Cards { get; } = new(StringComparer.Ordinal);

        /// <summary>The cards as the last committed save left them: what the book gives (rule 5).</summary>
        public SortedDictionary<string, Card> Committed { get; set; } = new(StringComparer.Ordinal);

        public SortedSet<string> DirtyCards { get; } = new(StringComparer.Ordinal);
        public List<BystialOpRecord> PendingOps { get; } = new();
        public InFlightBatch? InFlight { get; set; }

        /// <summary>Request keys applied since the character was attached (rule 5: a repeat gets the first answer).</summary>
        public HashSet<string> SeenOps { get; } = new(StringComparer.Ordinal);

        public BookBonuses Bonuses { get; set; } = BookBonuses.Empty;
        public int Version { get; set; }
    }
}
