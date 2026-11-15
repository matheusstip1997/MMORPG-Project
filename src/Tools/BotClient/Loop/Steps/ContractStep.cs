using System.Globalization;
using BotClient.Brain;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Crafting;
using Shared.Simulation.Economy;

namespace BotClient.Loop.Steps;

/// <summary>
/// Contracts (spec 5.4, M4 decisions 10 and 11; WP-M4-10), the pair's two halves. Both sides meet at a point first in their
/// scripts (<c>meet:contract</c>); game outcomes come from the server's messages only.
/// <list type="bullet">
/// <item><c>contract:propose:&lt;mold or recipe id&gt;[:&lt;gold&gt;][:expire]</c> (the customer): picks every input from its own Normal
/// bag (ClientCore's <see cref="Game.Gameplay.Entities.ClientForge.TryPickInputs"/>, the customer supplies every slot), proposes
/// with the gold (default <c>ContractSettings.minGold</c>) and the shortest deadline, waits for the commit (the gold and materials
/// in escrow), posts the contract id on the pair board (<c>contract.id</c>) and waits until its own contract list no longer holds
/// it after a crafter took it (completed: the output comes by mail, the script's next <c>mail:claim</c> takes it). With
/// <c>:expire</c> it posts nothing: it makes its open contracts due at once through the dev console (<c>/contracts expire</c>, a
/// server with the dev tools on) and waits until the contract left its list and the return mail arrived (the script's
/// <c>mail:claim</c> takes it back).</item>
/// <item><c>contract:craft</c> (the crafter): reads the partner's contract id, accepts it at the contract board, forges it at a
/// station of its definition through <see cref="ForgeRun"/> (a session owned by the contract: the inputs come from the escrow),
/// and checks that its gold rose by the contract gold less the contract fee (<c>FeeRates</c> <c>contract</c>).</item>
/// </list>
/// Counters: <c>contract.proposed</c>, <c>contract.accepted</c>, <c>contract.completed</c>, <c>contract.crafted</c>,
/// <c>contract.paid</c> (gold), <c>contract.expired</c>, <c>contract.refused.&lt;reason&gt;</c>. In a loop (not <c>loopOnce</c>) a round
/// without materials, gold, a partner's proposal or the job counts (<c>contract.noMaterials</c>, <c>.noGold</c>, <c>.noProposal</c>,
/// <c>.refused.&lt;reason&gt;</c>) and is done; in a script it fails.
/// </summary>
public sealed class ContractStep : IBotStep
{
    /// <summary>The pair board key the customer posts its contract id under.</summary>
    public const string IdKey = "contract.id";

    private const string ProposePrefix = "propose:";

    /// <summary>After the forge's result, the crafter's gold shows the payment once the inventory's gold message came.</summary>
    private const int PaidWaitMs = 10_000;

    /// <summary>
    /// After the contract closed, its mail (the output, or the return) reaches the mailbox a moment later (the mailbox reloads off
    /// the tick): the customer waits for the open mail count to rise, at most this long, so a following <c>mail:claim</c> finds it.
    /// </summary>
    private const int MailWaitMs = 10_000;

    private enum Phase : byte { Idle, Proposing, Waiting, Accepting, Listing, Forging, Paid }

    private readonly string _argument;
    private readonly bool _propose;
    private readonly string _defId = "";
    private readonly long _gold;
    private readonly bool _expire;
    private readonly string? _parseError;
    private readonly List<Shared.Protocol.ForgeInput> _inputs = new();
    private Phase _phase;
    private uint _pending;
    private uint _board;
    private long _contractId;
    private long _contractGold;
    private long _goldBefore;
    private long _waitUntilMs;
    private bool _seenTaken;
    private bool _devSent;
    private int _mailBefore = -1;
    private long _mailWaitUntilMs;
    private ForgeRun? _run;
    private static readonly HashSet<string> Handled = new(StringComparer.Ordinal);

    public ContractStep(string argument = "")
    {
        _argument = argument;
        if (argument == "craft")
            return;
        if (!argument.StartsWith(ProposePrefix, StringComparison.Ordinal))
        {
            _parseError = "contract:propose:<mold or recipe>[:<gold>][:expire] or contract:craft";
            return;
        }
        _propose = true;
        var parts = argument[ProposePrefix.Length..].Split(':');
        _defId = parts[0];
        foreach (var part in parts.Skip(1))
        {
            if (part == "expire")
                _expire = true;
            else if (long.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out long gold) && gold > 0)
                _gold = gold;
            else
                _parseError = $"contract:propose: '{part}' is neither a gold amount nor 'expire'";
        }
        if (_defId.Length == 0)
            _parseError = "contract:propose:<mold or recipe>";
    }

    public string Id => "contract";

    public void Begin(BotStepContext context)
    {
        _phase = Phase.Idle;
        _pending = 0;
        _board = 0;
        _contractId = 0;
        _seenTaken = false;
        _devSent = false;
        _mailBefore = -1;
        _mailWaitUntilMs = 0;
        _run = null;
        _waitUntilMs = context.NowMs + context.Behaviour.PairWaitMs;
    }

    public BotStepStatus Tick(BotStepContext context, ref BotCommand command)
    {
        if (_parseError != null)
            return context.Failed($"bad argument '{_argument}': {_parseError}");
        if (context.Contracts is not { } contracts)
            return context.NotAvailable(Id, "no contract client in this context");
        if (ContractSettings.Find(context.Data.Tables) is not { } settings)
            return context.NotAvailable(Id, "no ContractSettings table");
        return _propose ? Propose(context, contracts, settings) : Craft(context, contracts, ref command);
    }

    private BotStepStatus Propose(BotStepContext context, Game.Gameplay.Entities.ClientContracts contracts, ContractSettings settings)
    {
        switch (_phase)
        {
            case Phase.Proposing:
                if (contracts.ResultFor(_pending) is not { } result)
                    return BotStepStatus.Running;
                if (result.Reason != ContractReason.None)
                {
                    context.Count("contract.refused." + result.Reason);
                    return context.Failed("contract.refused." + result.Reason);
                }
                _contractId = result.ContractId;
                _mailBefore = context.Mail?.OpenCount ?? -1;
                context.Count("contract.proposed");
                context.Log($"contract {_contractId} proposed: {_defId} for {result.Gold} gold");
                if (_expire)
                {
                    // Due at once (WP-M4-10 dev console): the expiry job closes it through the one release path, by mail.
                    _devSent = context.SendReliable(ChatCodec.WriteSend(new ReliableMessageWriter(),
                        new ChatSendMessage { Text = "/contracts expire" }).ToArray());
                    context.Log("contract: > /contracts expire");
                }
                else
                    context.Pair?.Post(IdKey, _contractId.ToString(CultureInfo.InvariantCulture));
                _phase = Phase.Waiting;
                return BotStepStatus.Running;
            case Phase.Waiting:
                var mine = contracts.MineEntry(_contractId);
                if (mine != null)
                {
                    _seenTaken |= mine.Status is ContractStatus.Accepted or ContractStatus.InProgress;
                    return context.NowMs < _waitUntilMs ? BotStepStatus.Running
                        : context.Failed($"contract {_contractId} still {mine.Status} after {context.Behaviour.PairWaitMs} ms");
                }
                if (contracts.Mine == null)
                    return BotStepStatus.Running;
                if (!_seenTaken && !_expire)
                    return context.Failed($"contract {_contractId} closed before a crafter took it");
                if (_mailWaitUntilMs == 0)
                {
                    context.Count(_expire ? "contract.expired" : "contract.completed");
                    _mailWaitUntilMs = context.NowMs + MailWaitMs;
                }
                // Its mail (the output, or the return) arrives a moment after the close committed.
                if (_mailBefore >= 0 && context.Mail != null && context.Mail.OpenCount <= _mailBefore && context.NowMs < _mailWaitUntilMs)
                    return BotStepStatus.Running;
                return BotStepStatus.Done;
        }

        if (context.Forge.Rules is not { } rules)
            return context.NotAvailable(Id, "no forge tables");
        var definition = rules.Definitions.FirstOrDefault(d => d.Id == _defId);
        if (definition == null)
            return context.NotAvailable(Id, $"no mold or recipe '{_defId}'");
        var contents = context.Inventory.Contents;
        if (contents == null)
            return BotStepStatus.Running;
        if (context.InCombat || context.View.Dead)
            return BotStepStatus.Running;
        byte normal = (byte)(ContainerDef.Find(context.Data.Tables, ContainerDef.NormalBag)?.Code ?? 0);
        // Only what may go into a contract (R-5.4-03): never a stolen, tainted or non-tradable stack (a bag recovered with the stolen
        // mark leaves some; the server refuses them ContractNotTradable).
        var offerable = contents.Slots.Where(s => !s.Stolen && !s.Tainted && !s.NonTradable).ToList();
        if (!context.Forge.TryPickInputs(definition, offerable, normal, _inputs))
            return Soft(context, "contract.noMaterials", $"nothing in the Normal bag fills {_defId}");
        long gold = _gold > 0 ? _gold : settings.MinGold;
        if ((context.Outposts.Gold ?? 0) < gold)
            return Soft(context, "contract.noGold", $"{gold} gold needed");
        var slots = new List<ContractSlot>(_inputs.Count);
        for (int i = 0; i < _inputs.Count; i++)
        {
            var input = _inputs[i];
            var stack = contents.Slots.First(s => s.Slot == input.Slot);
            slots.Add(new ContractSlot
            {
                Supplier = ContractSupplier.Customer, Container = input.Container, Slot = input.Slot, ItemNetId = stack.ItemNetId,
                Count = (ushort)definition.Inputs[i].Count,
            });
        }
        context.SendReliable(contracts.ProposeRequest(definition.Kind, definition.NetId, gold, settings.MinDeadlineHours, slots, out _pending));
        _phase = Phase.Proposing;
        return BotStepStatus.Running;
    }

    private BotStepStatus Craft(BotStepContext context, Game.Gameplay.Entities.ClientContracts contracts, ref BotCommand command)
    {
        switch (_phase)
        {
            case Phase.Idle:
                if (context.Pair is not { } pair)
                    return context.Failed("contract:craft needs a pair scenario");
                if (!pair.TryRead(IdKey, out string text) || Handled.Contains(context.BotIndex + ":" + text)
                    || !long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out _contractId))
                    return context.NowMs < _waitUntilMs ? BotStepStatus.Running
                        : Soft(context, "contract.noProposal", $"no contract proposed by the partner within {context.Behaviour.PairWaitMs} ms");
                Handled.Add(context.BotIndex + ":" + text);
                _phase = Phase.Accepting;
                return BotStepStatus.Running;
            case Phase.Accepting when _pending == 0:
                if (context.InCombat || context.View.Dead)
                    return BotStepStatus.Running;
                switch (context.ApproachNpc(n => n.Offers(NpcInteraction.Contracts), ref command, out _board, out _))
                {
                    case NpcApproach.None:
                        context.Count("contract.noBoard");
                        return context.Failed("contract.noBoard: no contract board reached");
                    case NpcApproach.Walking:
                        return BotStepStatus.Running;
                }
                context.SendReliable(contracts.AcceptRequest(_board, _contractId, out _pending));
                return BotStepStatus.Running;
            case Phase.Accepting:
                if (contracts.ResultFor(_pending) is not { } accepted)
                    return BotStepStatus.Running;
                _pending = 0;
                if (accepted.Reason == ContractReason.InCombat)
                    return BotStepStatus.Running; // try again once out of combat
                if (accepted.Reason != ContractReason.None)
                    return Soft(context, "contract.refused." + accepted.Reason, $"the acceptance of contract {_contractId} was refused");
                _contractGold = accepted.Gold;
                context.Count("contract.accepted");
                _phase = Phase.Listing;
                return BotStepStatus.Running;
            case Phase.Listing:
                if (contracts.MineEntry(_contractId) is not { } entry)
                    return BotStepStatus.Running; // the own list comes after the acceptance committed
                if (context.Forge.Rules is not { } rules || !rules.TryGet(entry.Kind, entry.DefNetId, out var definition))
                    return context.Failed($"contract {_contractId}: unknown definition {entry.Kind} {entry.DefNetId}");
                _goldBefore = context.Outposts.Gold ?? 0;
                _run = new ForgeRun("contract", _ => false);
                _run.Begin();
                _run.UseContract(_contractId, definition);
                _phase = Phase.Forging;
                return BotStepStatus.Running;
            case Phase.Forging:
                var status = _run!.Tick(context, ref command);
                if (status != BotStepStatus.Done)
                    return status;
                context.Count("contract.crafted");
                _waitUntilMs = context.NowMs + PaidWaitMs;
                _phase = Phase.Paid;
                return BotStepStatus.Running;
            case Phase.Paid:
                long expected = _contractGold - ContractFee(context, _contractGold);
                long gained = (context.Outposts.Gold ?? 0) - _goldBefore;
                if (gained == expected)
                {
                    context.Count("contract.paid", gained);
                    context.Log($"contract {_contractId} done: paid {gained} gold ({_contractGold} less the fee)");
                    return BotStepStatus.Done;
                }
                return context.NowMs < _waitUntilMs ? BotStepStatus.Running
                    : context.Failed($"contract {_contractId}: gold rose by {gained}, expected {expected}");
        }
        return BotStepStatus.Running;
    }

    /// <summary>A round that cannot play: counted; a failure in a script (an exit entry never passes without it), done in a loop.</summary>
    private static BotStepStatus Soft(BotStepContext context, string counter, string why)
    {
        context.Count(counter);
        return context.Behaviour.LoopOnce ? context.Failed(counter + ": " + why) : BotStepStatus.Done;
    }

    /// <summary>The contract fee of <paramref name="gold"/> in an unowned territory (M4: every territory; FeeService's quote).</summary>
    private static long ContractFee(BotStepContext context, long gold)
    {
        var rate = context.Data.Rows<FeeRates>().FirstOrDefault(r => r.Id == "contract");
        return rate == null ? 0 : FeeMath.Total(FeeType.Contract, gold, rate.DefaultPercent, 0);
    }
}
