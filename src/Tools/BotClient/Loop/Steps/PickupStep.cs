using BotClient.Brain;
using Shared.Protocol;

namespace BotClient.Loop.Steps;

/// <summary>
/// The loop's pickup step: the M1 brain's looting alone (<see cref="BotBrainMode.Loot"/>): walk to its own drops (locked
/// to the killer or its party, R-4.3-02) and ask for them (<c>PickupRequest</c>). Done when no own drop was seen for
/// <c>loopPickupIdleMs</c>, or after <c>lootAfterKillMs</c> (the drops are no longer locked to it).
/// <para>
/// <c>pickup:roll</c> (WP-M4-6, spec 4.3 R-4.3-02 "M4 party loot"): in a party a member's pickup rolls among the eligible
/// members (the winner's bag, or mail when full), and the server sends every member each winner's part once the winner's
/// save committed it (<see cref="LootRollNotice"/>, ClientCore's <c>ClientParty.RollsSeen</c>). The entry loots like
/// <c>pickup</c> and is done once a roll part arrived since its last done (the fight before it loots too, so a roll seen
/// there counts). Outside a party it fails ("no party") in a script and is done with <c>pickup.noParty</c> in a loop; with
/// no roll by the idle time it is done with <c>pickup.noRoll</c> in a loop and fails ("no roll seen") in a script.
/// Counters (every part the bot hears of, whatever step runs): <c>pickup.rolls</c>, and for its own winnings
/// <c>pickup.rollUnitsWon</c> (into the bag) and <c>pickup.rollUnitsMailed</c>.
/// </para>
/// </summary>
public sealed class PickupStep : IBotStep
{
    private readonly string _argument;
    private long _beganAtMs;
    private long _lastDropAtMs;
    private int _rollsAtDone;

    public PickupStep(string argument = "")
    {
        _argument = argument;
        if (argument.Length > 0 && argument != "roll")
            throw new ArgumentException($"pickup step: '{argument}' is not 'roll'");
    }

    public string Id => "pickup";

    private bool Roll => _argument == "roll";

    public void Begin(BotStepContext context)
    {
        _beganAtMs = context.NowMs;
        _lastDropAtMs = context.NowMs;
    }

    public BotStepStatus Tick(BotStepContext context, ref BotCommand command)
    {
        long now = context.NowMs;
        if (Roll)
        {
            if (context.Party.RollsSeen > _rollsAtDone)
            {
                _rollsAtDone = context.Party.RollsSeen;
                return BotStepStatus.Done;
            }
            if (!context.Party.InParty)
            {
                context.Count("pickup.noParty");
                return context.Behaviour.LoopOnce ? context.Failed("no party") : BotStepStatus.Done;
            }
        }
        var loot = context.Brain.Think(context.View, BotBrainMode.Loot);
        if (context.Brain.SawDrop)
            _lastDropAtMs = now;
        else if (now - _lastDropAtMs >= context.Behaviour.LoopPickupIdleMs || now - _beganAtMs >= context.Behaviour.LootAfterKillMs)
        {
            if (!Roll)
                return BotStepStatus.Done;
            context.Count("pickup.noRoll");
            return context.Behaviour.LoopOnce ? context.Failed("no roll seen") : BotStepStatus.Done;
        }
        command = loot;
        return BotStepStatus.Running;
    }

    public void OnReliable(BotStepContext context, ReliableMessageId id, ReadOnlySpan<byte> message)
    {
        if (!Roll || id != ReliableMessageId.LootRollNotice || !ReliableMessages.TryRead(message, out LootRollNotice notice))
            return;
        context.Count("pickup.rolls");
        if (notice.WinnerEntityId != 0 && notice.WinnerEntityId == context.Entities.OwnEntityId)
        {
            context.Count("pickup.rollUnitsWon", notice.Units);
            context.Count("pickup.rollUnitsMailed", notice.Mailed);
        }
    }
}
