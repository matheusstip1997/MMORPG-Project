using GameServer.Combat;
using GameServer.Events;
using GameServer.Social;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Progression;

namespace GameServer.Progression;

/// <summary>Where a party member stands for a kill's exp share (WP-M4-2): in the world, alive, its position and entity.</summary>
public readonly record struct MemberPresence(bool InWorld, bool Alive, int XCm, int YCm, uint EntityId);

/// <summary>
/// Exp from kills (R-3.6-01, R-3.6-03; spec 3.6 <c>ExpService</c>, WP-M2-3; parties WP-M4-2). Consumes <see cref="Killed"/> in
/// phase 11, idempotent per event ID (<see cref="EventWatermark"/>). A character's gain is the monster's base exp x the rank
/// modifier (monster rank against the character's) x the band modifier (the character's level against its rank's band),
/// rounded down, with no floor, then its Bystial bonus. Level-ups, several in one kill allowed, give skill points (R-3.5-04)
/// and publish <see cref="LevelUp"/>; every gain publishes <see cref="ExpGained"/> for telemetry.
/// <list type="bullet">
/// <item>Players give no exp (R-3.6-01: kill monsters for exp); a kill with no character as actor gives nothing.</item>
/// <item>A killer in no party (the envelope's actor) gets its whole gain.</item>
/// <item>M4 (spec 6.5 "M4 party", M4 decision 1): a killer in a party shares the kill with every member that is attached,
/// alive and within <see cref="PartySettings.ExpShareRangeCm"/> of where the victim died (the killer always counts). Each gets
/// its own gain (its own rank and level, R-3.6-03) times its weight from <see cref="PartyExpSplit"/>: its share of the damage
/// the eligible members dealt (<see cref="IKillContributions"/>), at least <see cref="PartySettings.ExpFloorPercent"/>, the
/// weights scaled to one. Members in join order (rule 6).</item>
/// </list>
/// The monster's level, base exp and rank come from the world's current <see cref="CombatRules"/>, so a hot reload of
/// <c>MonsterDef</c> reaches the next kill.
/// </summary>
public sealed class ExpService : IEventConsumer<Killed>
{
    private readonly ProgressionStore _store;
    private readonly Func<CombatRules?> _combat;
    private readonly EventBus? _events;
    private readonly long[] _members = new long[ReliableMessages.MaxPartyMembers];
    private readonly long[] _eligible = new long[ReliableMessages.MaxPartyMembers];
    private readonly uint[] _entities = new uint[ReliableMessages.MaxPartyMembers];
    private readonly long[] _damage = new long[ReliableMessages.MaxPartyMembers];
    private readonly long[] _weights = new long[ReliableMessages.MaxPartyMembers];
    private EventWatermark _watermark;

    public ExpService(ProgressionStore store, Func<CombatRules?> combat, EventBus? events)
    {
        _store = store;
        _combat = combat;
        _events = events;
    }

    /// <summary>
    /// Whole percents more exp from a kill of the monster (the character's Bystial book against the monster's family, spec
    /// 14.2 R-14.2-05, WP-M2-8), applied after the rank and band modifiers; null without the book.
    /// </summary>
    public Func<long, MonsterDef, int>? BonusPercent { get; set; }

    /// <summary>The world's parties (WP-M4-2); without them every kill pays its killer alone.</summary>
    public IPartyQuery Parties { get; set; } = NoParties.Instance;

    /// <summary>The party share rules (WP-M4-2); null without parties.</summary>
    public PartySettings? PartyRules { get; set; }

    /// <summary>The damage each character dealt to a victim (WP-M4-2); null: every eligible member weighs the same.</summary>
    public IKillContributions? Contributions { get; set; }

    /// <summary>A character's presence in the world (WP-M4-2); a member not in the world gets no share.</summary>
    public Func<long, MemberPresence> Presence { get; set; } = _ => default;

    public void Subscribe(EventBus events) => events.Subscribe<Killed>(this);

    public void Handle(in EventEnvelope envelope, in Killed payload)
    {
        if (!_watermark.TryAccept(envelope))
            return; // a re-dispatched kill pays nothing twice
        long characterId = envelope.ActorCharacterId;
        if (characterId == 0 || payload.VictimCharacterId != 0 || !_store.IsAttached(characterId))
            return;
        if (_combat() is not { } combat || !combat.TryGetMonster(payload.VictimDefId, out var monster))
            return;

        long partyId = PartyRules == null ? 0 : Parties.PartyIdOf(characterId);
        if (partyId == 0)
        {
            Pay(characterId, payload.KillerId, monster, ExpMath.PpmOne, payload.VictimDefId, envelope.RegionId);
            return;
        }

        int eligible = Eligible(partyId, characterId, payload);
        for (int i = 0; i < eligible; i++)
            _damage[i] = Contributions?.DamageOf(payload.VictimId, _eligible[i]) ?? 0;
        PartyExpSplit.Weights(_damage.AsSpan(0, eligible), PartyRules!.ExpFloorPercent, _weights);
        for (int i = 0; i < eligible; i++)
            Pay(_eligible[i], _entities[i], monster, _weights[i], payload.VictimDefId, envelope.RegionId);
    }

    /// <summary>The killer and every member attached, alive and within range of the kill, in join order.</summary>
    private int Eligible(long partyId, long killerId, in Killed payload)
    {
        int count = Parties.MembersOf(partyId, _members);
        long range = PartyRules!.ExpShareRangeCm;
        int eligible = 0;
        for (int i = 0; i < count; i++)
        {
            long member = _members[i];
            if (member == killerId)
            {
                _eligible[eligible] = member;
                _entities[eligible++] = payload.KillerId;
                continue;
            }
            if (!_store.IsAttached(member))
                continue;
            var presence = Presence(member);
            if (!presence.InWorld || !presence.Alive)
                continue;
            long dx = presence.XCm - payload.XCm, dy = presence.YCm - payload.YCm;
            if (dx * dx + dy * dy > range * range)
                continue;
            _eligible[eligible] = member;
            _entities[eligible++] = presence.EntityId;
        }
        return eligible;
    }

    /// <summary>The character's own gain from the monster (its rank and band, its book) times its weight.</summary>
    private void Pay(long characterId, uint entityId, MonsterDef monster, long weightPpm, int victimDefId, int regionId)
    {
        var curves = _store.Rules.Curves;
        var before = _store.Snapshot(characterId);
        int rankPercent = curves.RankPercent(before.Rank, monster.Rank);
        long bandPpm = curves.BandPpm(before.Level, before.Rank);
        long gain = ExpMath.Gain(curves.BaseExpOf(monster), rankPercent, bandPpm);
        if (BonusPercent?.Invoke(characterId, monster) is int bonus and not 0)
            gain = Shared.Simulation.Bystial.BystialMath.ApplyPercent(gain, bonus);
        if (weightPpm != ExpMath.PpmOne)
            gain = PartyExpSplit.Share(gain, weightPpm);
        Apply(characterId, entityId, gain, victimDefId, rankPercent, bandPpm, regionId);
    }

    /// <summary>
    /// Exp from another source, already computed (WP-M2-6: run claims pay through here). No modifiers are applied.
    /// Returns the levels gained.
    /// </summary>
    public int Grant(long characterId, uint entityId, long amount, int regionId = 0)
    {
        if (amount < 0)
            throw new ArgumentOutOfRangeException(nameof(amount));
        return _store.IsAttached(characterId) ? Apply(characterId, entityId, amount, 0, 100, ExpMath.PpmOne, regionId) : 0;
    }

    private int Apply(long characterId, uint entityId, long gain, int victimDefId, int rankPercent, long bandPpm, int regionId)
    {
        int fromLevel = _store.AddExp(characterId, gain, out int levelsGained);
        if (_events == null)
            return levelsGained;
        _events.Publish(new ExpGained(characterId, entityId, gain, victimDefId, rankPercent, bandPpm), characterId, regionId);
        if (levelsGained > 0)
            _events.Publish(new LevelUp(characterId, entityId, fromLevel, fromLevel + levelsGained), characterId, regionId);
        return levelsGained;
    }
}
