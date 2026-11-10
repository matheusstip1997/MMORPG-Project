using BotClient.Brain;

namespace BotClient.Loop.Steps;

/// <summary>
/// The loop's pickup step: the M1 brain's looting alone (<see cref="BotBrainMode.Loot"/>): walk to its own drops (locked
/// to the killer, R-4.3-02) and ask for them (<c>PickupRequest</c>). Done when no own drop was seen for
/// <c>loopPickupIdleMs</c>, or after <c>lootAfterKillMs</c> (the drops are no longer locked to it).
/// <para>
/// <c>pickup:roll</c> (WP-M4-12 placeholder; made real by WP-M4-6, spec 4.3 R-4.3-02): in a party the pickup of a drop rolls
/// among the eligible members (the winner's bag, or mail when full) and the party chat names the winner; the entry is done
/// once the bot saw a roll of its pair's drops. Until then "not yet" (no party loot yet, WP-M4-6); the WP replaces this branch.
/// </para>
/// </summary>
public sealed class PickupStep : IBotStep
{
    private readonly string _argument;
    private long _beganAtMs;
    private long _lastDropAtMs;

    public PickupStep(string argument = "")
    {
        _argument = argument;
        if (argument.Length > 0 && argument != "roll")
            throw new ArgumentException($"pickup step: '{argument}' is not 'roll'");
    }

    public string Id => "pickup";

    public void Begin(BotStepContext context)
    {
        _beganAtMs = context.NowMs;
        _lastDropAtMs = context.NowMs;
    }

    public BotStepStatus Tick(BotStepContext context, ref BotCommand command)
    {
        if (_argument == "roll")
            return context.NotAvailable(Id, "no party loot roll yet, WP-M4-6; argument 'roll'");
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
