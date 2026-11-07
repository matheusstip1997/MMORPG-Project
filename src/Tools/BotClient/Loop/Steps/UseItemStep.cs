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
/// <para>
/// <c>"useItem:&lt;ItemDef id&gt;@&lt;spawn marker&gt;"</c> is the M3 exit criterion's drink (plan section 1: "a vendor potion is drunk in
/// combat: HP rises and TOX rises"; WP-M3-12): the bot fights at the camp until it is in combat with HP missing, drinks, and the
/// entry is done only when, after the committed answer, its HP rose by the heal (or all it missed) and its toxicity rose
/// (<c>OwnStats</c>). Holding none of the item fails the entry; so do <c>useItem.noHpRise</c> and <c>useItem.noToxRise</c> if
/// <see cref="ConfirmMs"/> pass without them. Counters <c>useItem.inCombat</c>, <c>useItem.hpBefore</c>, <c>useItem.hpAfter</c>,
/// <c>useItem.toxBeforeMilli</c>, <c>useItem.toxAfterMilli</c>.
/// </para>
/// Counters: <c>useItem.used</c>, <c>useItem.refused.&lt;reason&gt;</c>; done at once with <c>useItem.off</c> (no threshold),
/// <c>useItem.notNeeded</c> (HP above it or no own meters yet), <c>useItem.none</c> (no HP potion, or none of the
/// argument's item, held) or <c>useItem.skipped.&lt;reason&gt;</c> (the client's check: on cooldown, a use still waiting).
/// </summary>
public sealed class UseItemStep : IBotStep
{
    /// <summary>How long after the answer the combat form waits for the HP and toxicity to show (the tool's patience, not game data).</summary>
    public const long ConfirmMs = 5_000;

    private readonly string _argument;
    private readonly string _camp;
    private uint _pending;
    private Shared.Simulation.FixVec2? _homeBefore;
    private ushort _combatNetId;
    private bool _answered;
    private long _answeredAtMs;
    private int _hpAtSend;
    private int _hpLowest;
    private int _hpHighest;
    private int _hpRise;
    private long _toxAtSend;

    /// <param name="argument">
    /// A consumable's <c>ItemDef</c> id to use now, with <c>@&lt;spawn marker&gt;</c> to drink it in combat at that camp; empty for the
    /// HP threshold rule.
    /// </param>
    public UseItemStep(string argument = "")
    {
        argument ??= string.Empty;
        int at = argument.IndexOf('@');
        _argument = at >= 0 ? argument.Substring(0, at) : argument;
        _camp = at >= 0 ? argument.Substring(at + 1) : string.Empty;
        if (at >= 0 && (_argument.Length == 0 || _camp.Length == 0))
            throw new ArgumentException($"useItem step: '{argument}' needs an item and a spawn marker around @");
    }

    public string Id => "useItem";

    public void Begin(BotStepContext context)
    {
        _pending = 0;
        _answered = false;
        _homeBefore = null;
        _combatNetId = 0;
        if (_camp.Length > 0 && context.Data.Markers.TryGetValue(_camp, out var camp))
        {
            _homeBefore = context.Brain.Home;
            context.Brain.SetHome(camp);
        }
    }

    public BotStepStatus Tick(BotStepContext context, ref BotCommand command)
    {
        if (context.Consumables is not { } consumables)
            return End(context, context.NotAvailable(Id, "no ClientConsumables in this context"));
        if (_camp.Length > 0)
            return TickInCombat(context, consumables, ref command);
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

    /// <summary>The combat form: fight at the camp until hurt and in combat, drink, then wait for the answer and the numbers to show.</summary>
    private BotStepStatus TickInCombat(BotStepContext context, Game.Gameplay.Entities.ClientConsumables consumables, ref BotCommand command)
    {
        if (!context.Data.Markers.ContainsKey(_camp))
            return End(context, context.NotAvailable(Id, $"no spawn marker '{_camp}'"));
        if (context.Stats is not { } stats)
            return End(context, context.NotAvailable(Id, "no ClientStats in this context"));
        var own = context.View.Own;
        if (_pending != 0)
            return ConfirmInCombat(context, consumables, stats, own);

        int wanted = consumables.NetIdOf(_argument);
        if (wanted == 0)
        {
            context.Count("useItem.notConsumable");
            return End(context, context.Failed($"'{_argument}' is no consumable"));
        }
        if (!FindItem(context, wanted, out byte slot))
        {
            context.Count("useItem.none");
            return End(context, context.Failed($"no {_argument} held"));
        }
        var ready = consumables.Check(wanted, stats, context.NowMs);
        bool hurt = context.View.HasResources && !context.View.Dead && own.MaxHp > 0 && own.Hp < own.MaxHp;
        if (!hurt || !context.InCombat || !context.View.ActionIdle || ready == UseItemReason.OnCooldown || ready == UseItemReason.UseInProgress)
        {
            command = context.Brain.Think(context.View, BotBrainMode.All);
            return BotStepStatus.Running;
        }
        if (ready != UseItemReason.None)
        {
            context.Count("useItem.skipped." + ready);
            return End(context, context.Failed($"the client's check refused it: {ready}"));
        }
        _combatNetId = (ushort)wanted;
        _hpAtSend = _hpLowest = _hpHighest = own.Hp;
        _hpRise = 0;
        _toxAtSend = stats.ToxicityMilli(context.NowMs);
        context.Count("useItem.inCombat");
        context.SendReliable(consumables.UseRequest(0, slot, _combatNetId, context.NowMs, out _pending));
        if (consumables.TryGet(_combatNetId, out var def))
            context.PlayAction?.Invoke(def.ActionId);
        return BotStepStatus.Running;
    }

    private BotStepStatus ConfirmInCombat(BotStepContext context, Game.Gameplay.Entities.ClientConsumables consumables,
        Game.Gameplay.Entities.ClientStats stats, OwnResources own)
    {
        // The rise from the lowest HP since the request: a hit during the drink does not hide the heal.
        _hpLowest = Math.Min(_hpLowest, own.Hp);
        _hpHighest = Math.Max(_hpHighest, own.Hp);
        _hpRise = Math.Max(_hpRise, own.Hp - _hpLowest);
        if (!_answered)
        {
            if (consumables.LastResult is not { } result || result.RequestId != _pending)
            {
                if (consumables.IsWaiting(context.NowMs))
                    return BotStepStatus.Running;
                context.Count("useItem.noAnswer");
                return End(context, context.Failed("no answer to the use request"));
            }
            if (result.Reason != UseItemReason.None)
            {
                context.Count("useItem.refused." + result.Reason);
                return End(context, context.Failed($"refused: {result.Reason}"));
            }
            context.Count("useItem.used");
            _answered = true;
            _answeredAtMs = context.NowMs;
        }
        consumables.TryGet(_combatNetId, out var def);
        long toxNow = stats.ToxicityMilli(context.NowMs);
        int heal = def?.HealHp ?? 0;
        bool hpRose = _hpRise >= Math.Max(1, Math.Min(heal, own.MaxHp - _hpAtSend));
        bool toxRose = toxNow > _toxAtSend;
        if (hpRose && toxRose)
        {
            context.Count("useItem.hpBefore", _hpAtSend);
            context.Count("useItem.hpAfter", _hpHighest);
            context.Count("useItem.toxBeforeMilli", _toxAtSend);
            context.Count("useItem.toxAfterMilli", toxNow);
            return End(context, BotStepStatus.Done);
        }
        if (context.NowMs - _answeredAtMs < ConfirmMs)
            return BotStepStatus.Running;
        context.Count(hpRose ? "useItem.noToxRise" : "useItem.noHpRise");
        return End(context, context.Failed(hpRose ? $"toxicity did not rise from {_toxAtSend}" : $"HP did not rise from {_hpAtSend}"));
    }

    /// <summary>Back to the camp of its level, then <paramref name="status"/> (a not-available return too: its End is not called).</summary>
    private BotStepStatus End(BotStepContext context, BotStepStatus status)
    {
        if (_homeBefore is { } home)
            context.Brain.SetHome(home);
        _homeBefore = null;
        return status;
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
