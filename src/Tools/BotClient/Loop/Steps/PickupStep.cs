using BotClient.Brain;

namespace BotClient.Loop.Steps;

/// <summary>
/// The loop's pickup step: the M1 brain's looting alone (<see cref="BotBrainMode.Loot"/>): walk to its own drops (locked
/// to the killer, R-4.3-02) and ask for them (<c>PickupRequest</c>). Done when no own drop was seen for
/// <c>loopPickupIdleMs</c>, or after <c>lootAfterKillMs</c> (the drops are no longer locked to it).
/// </summary>
public sealed class PickupStep : IBotStep
{
    private long _beganAtMs;
    private long _lastDropAtMs;

    public string Id => "pickup";

    public void Begin(BotStepContext context)
    {
        _beganAtMs = context.NowMs;
        _lastDropAtMs = context.NowMs;
    }

    public BotStepStatus Tick(BotStepContext context, ref BotCommand command)
    {
        long now = context.NowMs;
        var loot = context.Brain.Think(context.View, BotBrainMode.Loot);
        if (context.Brain.SawDrop)
            _lastDropAtMs = now;
        else if (now - _lastDropAtMs >= context.Behaviour.LoopPickupIdleMs || now - _beganAtMs >= context.Behaviour.LootAfterKillMs)
            return BotStepStatus.Done;
        command = loot;
        return BotStepStatus.Running;
    }
}
