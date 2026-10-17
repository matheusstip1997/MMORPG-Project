using GameServer.World;

namespace GameServer.Events;

/// <summary>Tick phase 11 (spec 1.6): delivers the events the world's bus queued during phases 1-10.</summary>
public sealed class EventDispatchHook(EventBus bus) : ITickHook
{
    public void Run(uint serverTick) => bus.DispatchQueued();
}
