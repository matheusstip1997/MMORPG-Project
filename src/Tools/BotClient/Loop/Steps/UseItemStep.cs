using BotClient.Brain;
using Shared.Protocol;

namespace BotClient.Loop.Steps;

/// <summary>
/// The loop's useItem step (WP-M3-3; spec 3.4 consumable use): below <c>loopDrinkBelowPercent</c> of its max HP the bot
/// drinks an HP potion it holds (the behaviour's <c>loopBuyItemId</c> when it heals, else the first consumable that heals
/// HP): one <c>UseItemRequest</c> once its own action is idle, played by the predictor like a skill
/// (<c>LocalPlayerPredictor.RequestAction</c>), answered once the use committed. The server refuses what the client's
/// check cannot know (toxicity, cooldown after a reconnect).
/// <para>
/// With an argument (<c>"useItem:&lt;ItemDef id&gt;"</c>, a script entry such as the M3 exit run's) it uses that consumable
/// now, whatever the HP: the lowest Normal bag stack of it. A use right after a fight is a use in combat (spec 12.7 combat
/// state).
/// </para>
/// Counters: <c>useItem.used</c>, <c>useItem.refused.&lt;reason&gt;</c>; done at once with <c>useItem.off</c> (no threshold),
/// <c>useItem.notNeeded</c> (HP above it or no own meters yet), <c>useItem.none</c> (no HP potion, or none of the
/// argument's item, held) or <c>useItem.skipped.&lt;reason&gt;</c> (the client's check: on cooldown, a use still waiting).
/// </summary>
public sealed class UseItemStep : IBotStep
{
    private readonly string _argument;
    private uint _pending;

    /// <param name="argument">A consumable's <c>ItemDef</c> id to use now; empty for the HP threshold rule.</param>
    public UseItemStep(string argument = "")
    {
        _argument = argument ?? string.Empty;
    }

    public string Id => "useItem";

    public void Begin(BotStepContext context) => _pending = 0;

    public BotStepStatus Tick(BotStepContext context, ref BotCommand command)
    {
        if (context.Consumables is not { } consumables)
            return context.NotAvailable(Id, "no ClientConsumables in this context");
        if (_pending != 0)
            return Answer(context, consumables);

        byte slot;
        ushort netId;
        if (_argument.Length > 0)
        {
            int wanted = consumables.NetIdOf(_argument);
            if (wanted == 0)
            {
                context.Count("useItem.notConsumable");
                return BotStepStatus.Failed;
            }
            if (!FindItem(context, wanted, out slot))
            {
                context.Count("useItem.none");
                return BotStepStatus.Done;
            }
            netId = (ushort)wanted;
            return Send(context, consumables, slot, netId);
        }

        int threshold = context.Behaviour.LoopDrinkBelowPercent;
        if (threshold <= 0)
        {
            context.Count("useItem.off");
            return BotStepStatus.Done;
        }
        var own = context.View.Own;
        if (!context.View.HasResources || own.MaxHp <= 0 || context.View.Dead || (long)own.Hp * 100 >= (long)own.MaxHp * threshold)
        {
            context.Count("useItem.notNeeded");
            return BotStepStatus.Done;
        }
        if (!FindPotion(context, consumables, out slot, out netId))
        {
            context.Count("useItem.none");
            return BotStepStatus.Done;
        }
        return Send(context, consumables, slot, netId);
    }

    /// <summary>The client's check, then the request once the bot's own action is idle, and the drink played in the predictor.</summary>
    private BotStepStatus Send(BotStepContext context, Game.Gameplay.Entities.ClientConsumables consumables, byte slot, ushort netId)
    {
        var reason = consumables.Check(netId, null, context.NowMs);
        if (reason != UseItemReason.None)
        {
            context.Count("useItem.skipped." + reason);
            return BotStepStatus.Done;
        }
        if (!context.View.ActionIdle)
            return BotStepStatus.Running; // the server refuses a use during another action (ActionBusy)
        context.SendReliable(consumables.UseRequest(0, slot, netId, context.NowMs, out _pending));
        if (consumables.TryGet(netId, out var def))
            context.PlayAction?.Invoke(def.ActionId);
        return BotStepStatus.Running;
    }

    /// <summary>The lowest Normal bag slot holding <paramref name="netId"/>.</summary>
    private static bool FindItem(BotStepContext context, int netId, out byte slot)
    {
        slot = 0;
        bool found = false;
        foreach (var entry in context.Inventory.Contents?.Slots ?? [])
        {
            if (entry.Count > 0 && entry.ItemNetId == netId && (!found || entry.Slot < slot))
            {
                slot = entry.Slot;
                found = true;
            }
        }
        return found;
    }

    /// <summary>The Normal bag slot of an HP potion: the behaviour's buy item first, else the lowest slot that heals HP.</summary>
    private static bool FindPotion(BotStepContext context, Game.Gameplay.Entities.ClientConsumables consumables, out byte slot,
        out ushort netId)
    {
        slot = 0;
        netId = 0;
        int preferred = consumables.NetIdOf(context.Behaviour.LoopBuyItemId);
        bool found = false;
        foreach (var entry in context.Inventory.Contents?.Slots ?? [])
        {
            if (entry.Count == 0 || !consumables.TryGet(entry.ItemNetId, out var def) || def.HealHp <= 0)
                continue;
            bool better = !found || (entry.ItemNetId == preferred && netId != preferred)
                          || ((entry.ItemNetId == preferred) == (netId == preferred) && entry.Slot < slot);
            if (!better)
                continue;
            slot = entry.Slot;
            netId = entry.ItemNetId;
            found = true;
        }
        return found;
    }

    private BotStepStatus Answer(BotStepContext context, Game.Gameplay.Entities.ClientConsumables consumables)
    {
        if (consumables.LastResult is not { } result || result.RequestId != _pending)
            return consumables.IsWaiting(context.NowMs) ? BotStepStatus.Running : Lost(context);
        if (result.Reason != UseItemReason.None)
        {
            context.Count("useItem.refused." + result.Reason);
            return BotStepStatus.Failed;
        }
        context.Count("useItem.used");
        return BotStepStatus.Done;
    }

    private static BotStepStatus Lost(BotStepContext context)
    {
        context.Count("useItem.noAnswer");
        return BotStepStatus.Failed;
    }
}
