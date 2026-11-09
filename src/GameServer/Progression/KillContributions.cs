using GameServer.Events;

namespace GameServer.Progression;

/// <summary>
/// The damage each character dealt to a victim (spec 6.5 "M4 party", R-3.6-03; WP-M4-2): what the party exp split reads
/// (<see cref="ExpService"/>), and WP-M4-6's "a hit on the monster" for the party loot roll.
/// </summary>
public interface IKillContributions
{
    /// <summary>HP the character took from the victim since it was first hit (0 when none, or the ledger was full or forgotten).</summary>
    long DamageOf(uint victimEntityId, long characterId);

    /// <summary>Whether the character hit the victim.</summary>
    bool HasHit(uint victimEntityId, long characterId);
}

/// <summary>
/// Keeps the damage ledger of every victim from <see cref="Damaged"/> events (the envelope's actor is the attacking
/// character; monsters' damage, actor 0, is not kept), at most <c>PartySettings.maxContributors</c> characters per victim,
/// and drops a victim's ledger after its <see cref="Killed"/> (subscribe this last, so every <see cref="Killed"/> consumer
/// reads it first: the bus delivers one event to its consumers in subscription order, and a victim's hits are published
/// before its death) or once nobody hit it for <c>PartySettings.contributionForgetMs</c> (a monster that never died).
/// Ledgers are pooled; the event path allocates nothing once warm. Idempotent per event (watermarks), world thread only.
/// </summary>
public sealed class KillContributions : IKillContributions, IEventConsumer<Damaged>, IEventConsumer<Killed>
{
    private readonly int _maxContributors;
    private readonly long _forgetMs;
    private readonly Func<long> _nowMs;
    private readonly Dictionary<uint, Ledger> _ledgers = new();
    private readonly Stack<Ledger> _pool = new();
    private readonly List<uint> _expired = new();
    private EventWatermark _damaged;
    private EventWatermark _killed;
    private long _nextSweepMs;

    public KillContributions(int maxContributors, int forgetMs, Func<long> nowMs)
    {
        if (maxContributors < 1)
            throw new ArgumentOutOfRangeException(nameof(maxContributors));
        _maxContributors = maxContributors;
        _forgetMs = forgetMs;
        _nowMs = nowMs;
    }

    /// <summary>Victims with a ledger now (tests).</summary>
    public int Count => _ledgers.Count;

    public void Subscribe(EventBus events)
    {
        events.Subscribe<Damaged>(this);
        events.Subscribe<Killed>(this);
    }

    public long DamageOf(uint victimEntityId, long characterId)
    {
        if (!_ledgers.TryGetValue(victimEntityId, out var ledger))
            return 0;
        int index = ledger.IndexOf(characterId);
        return index < 0 ? 0 : ledger.Damage[index];
    }

    public bool HasHit(uint victimEntityId, long characterId) =>
        _ledgers.TryGetValue(victimEntityId, out var ledger) && ledger.IndexOf(characterId) >= 0;

    public void Handle(in EventEnvelope envelope, in Damaged payload)
    {
        if (!_damaged.TryAccept(envelope))
            return;
        long now = _nowMs();
        if (now >= _nextSweepMs)
            Sweep(now);
        long characterId = envelope.ActorCharacterId;
        if (characterId == 0 || payload.Amount <= 0)
            return;
        if (!_ledgers.TryGetValue(payload.TargetId, out var ledger))
        {
            ledger = _pool.Count > 0 ? _pool.Pop() : new Ledger(_maxContributors);
            _ledgers.Add(payload.TargetId, ledger);
        }
        ledger.LastHitMs = now;
        int index = ledger.IndexOf(characterId);
        if (index >= 0)
            ledger.Damage[index] += payload.Amount;
        else if (ledger.Count < ledger.Characters.Length)
        {
            ledger.Characters[ledger.Count] = characterId;
            ledger.Damage[ledger.Count] = payload.Amount;
            ledger.Count++;
        }
    }

    public void Handle(in EventEnvelope envelope, in Killed payload)
    {
        if (!_killed.TryAccept(envelope))
            return;
        if (_ledgers.Remove(payload.VictimId, out var ledger))
            Return(ledger);
    }

    private void Sweep(long now)
    {
        _nextSweepMs = now + _forgetMs;
        _expired.Clear();
        foreach (var pair in _ledgers)
        {
            if (now - pair.Value.LastHitMs >= _forgetMs)
                _expired.Add(pair.Key);
        }
        foreach (uint victim in _expired)
        {
            if (_ledgers.Remove(victim, out var ledger))
                Return(ledger);
        }
    }

    private void Return(Ledger ledger)
    {
        ledger.Count = 0;
        _pool.Push(ledger);
    }

    private sealed class Ledger
    {
        public Ledger(int capacity)
        {
            Characters = new long[capacity];
            Damage = new long[capacity];
        }

        public long[] Characters { get; }

        public long[] Damage { get; }

        public int Count { get; set; }

        public long LastHitMs { get; set; }

        public int IndexOf(long characterId)
        {
            for (int i = 0; i < Count; i++)
            {
                if (Characters[i] == characterId)
                    return i;
            }
            return -1;
        }
    }
}
