using GameServer.Entities;
using GameServer.Events;

namespace GameServer.AI;

/// <summary>
/// Feeds combat facts to the monster AI (rule 3: systems react to bus events, not to each other's internals). A player
/// that damages a monster is remembered on the monster's <see cref="MonsterBrain"/>, so an idle monster turns on an
/// attacker outside its aggro range at its next think (user decision 2026-10-22). Delivered in phase 11; idempotent:
/// the same event twice sets the same id. The last damaging player of the tick wins.
/// </summary>
internal sealed class MonsterAiEvents : IEventConsumer<Damaged>
{
    private readonly Func<uint, Entity?> _lookup;

    public MonsterAiEvents(Func<uint, Entity?> lookup)
    {
        _lookup = lookup;
    }

    public void Subscribe(EventBus bus) => bus.Subscribe<Damaged>(this);

    public void Handle(in EventEnvelope envelope, in Damaged payload)
    {
        if (_lookup(payload.TargetId) is Monster { Brain.Mode: MonsterMode.Idle } monster && _lookup(payload.SourceId) is Player)
            monster.Brain.RetaliateId = payload.SourceId;
    }
}
