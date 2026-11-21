using System.Globalization;
using BotClient.Brain;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Contracts;
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
/// <item><c>contract:propose:enchant[:&lt;ItemDef id&gt;][:&lt;gold&gt;][:stones=crafter][:expire]</c> (the customer, WP-M4-10 part C):
/// an enchant contract on one of its own items: the first enchantable instance in the Safe bag's equipment area (never an
/// equipped one: the escrow takes no worn gear), of that item id when named, below +9; slot 0 is that instance and slot 1 the
/// stones of its tier (<see cref="ContractRules.CheckEnchantTerms"/>), from its Normal bag, or from the crafter with
/// <c>stones=crafter</c>. Then as above: the id posted, the wait, the same instance back by mail at its new level.</item>
/// <item><c>contract:craft</c> (the crafter): reads the partner's contract id, accepts it at the contract board, forges it at a
/// station of its definition through <see cref="ForgeRun"/> (a session owned by the contract: the inputs come from the escrow),
/// and checks that its gold rose by the contract gold less the contract fee (<c>FeeRates</c> <c>contract</c>). An enchant
/// contract (<see cref="ForgeKind.Enchant"/>) is one attempt instead: at a station of the item's job, out of combat, one
/// <c>EnchantRequest</c> naming the contract (the stones from its own Normal bag when the terms say the crafter supplies them),
/// its answer after the commit, then the same pay check.</item>
/// <item><c>contract:craft:expire</c> (the crafter, QA M4 wave 2 D2): accepts as above, starts the work of a forge contract (a
/// session naming the contract: the work started is saved at once), cancels the session, makes the contract due through the dev
/// console (<c>/contracts expire</c>) and checks that its gold fell by <c>ContractSettings.cancelCostPercent</c> of the contract
/// gold (the expiry charge of a started contract). The customer's proposal for it takes <c>:crafterExpires</c>, so its close
/// counts as an expiry and its return mail is claimed by the next <c>mail:claim</c>.</item>
/// </list>
/// Counters: <c>contract.proposed</c>, <c>contract.accepted</c>, <c>contract.completed</c>, <c>contract.crafted</c>,
/// <c>contract.enchanted</c>, <c>contract.paid</c> (gold), <c>contract.expired</c>, <c>contract.expiryCharged</c> (gold), <c>contract.refused.&lt;reason&gt;</c>,
/// <c>contract.enchantRefused.&lt;reason&gt;</c>. In a loop (not <c>loopOnce</c>) a round without materials, an item to enchant,
/// stones, gold, a partner's proposal, the job or the station counts (<c>contract.noMaterials</c>, <c>.noItem</c>, <c>.noStones</c>,
/// <c>.noGold</c>, <c>.noProposal</c>, <c>.noStation</c>, <c>.refused.&lt;reason&gt;</c>) and is done; in a script it fails.
/// </summary>
public sealed class ContractStep : IBotStep
{
    /// <summary>The pair board key the customer posts its contract id under.</summary>
    public const string IdKey = "contract.id";

    private const string ProposePrefix = "propose:";

    /// <summary>The definition argument of an enchant contract's proposal (<c>contract:propose:enchant...</c>).</summary>
    public const string EnchantArgument = "enchant";

    private const string CrafterStones = "stones=crafter";
    private const string CustomerStones = "stones=customer";

    /// <summary>After the forge's result, the crafter's gold shows the payment once the inventory's gold message came.</summary>
    private const int PaidWaitMs = 10_000;

    private const string CrafterExpires = "crafterExpires";

    /// <summary>
    /// After the contract closed, its mail (the output, or the return) reaches the mailbox a moment later (the mailbox reloads off
    /// the tick): the customer waits for the open mail count to rise, at most this long, so a following <c>mail:claim</c> finds it.
    /// </summary>
    private const int MailWaitMs = 10_000;

    private enum Phase : byte { Idle, Proposing, Waiting, Accepting, Listing, Forging, Enchanting, Paid, Expiring }

    private readonly string _argument;
    private readonly bool _propose;
    private readonly string _defId = "";
    private readonly long _gold;
    private readonly bool _expire;
    private readonly bool _crafterExpires;
    private readonly bool _expireAfterStart;
    private long _expireAtMs;
    private readonly bool _enchant;
    private readonly string _itemId = "";
    private readonly bool _crafterStones;
    private readonly string? _parseError;
    private readonly List<Shared.Protocol.ForgeInput> _inputs = new();
    private readonly List<ContractSlot> _slots = new();
    private Phase _phase;
    private uint _pending;
    private uint _enchantPending;
    private string _label = "";
    private ItemDef? _enchantItem;
    private ItemDef? _crafterStone;
    private int _crafterStoneCount;
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
        if (argument == "craft:expire")
        {
            _expireAfterStart = true;
            return;
        }
        if (!argument.StartsWith(ProposePrefix, StringComparison.Ordinal))
        {
            _parseError = "contract:propose:<mold or recipe>[:<gold>][:expire], "
                          + "contract:propose:enchant[:<item>][:<gold>][:stones=crafter][:expire], contract:craft or contract:craft:expire";
            return;
        }
        _propose = true;
        var parts = argument[ProposePrefix.Length..].Split(':');
        _defId = parts[0];
        _enchant = _defId == EnchantArgument;
        foreach (var part in parts.Skip(1))
        {
            if (part == "expire")
                _expire = true;
            else if (part == CrafterExpires)
                _crafterExpires = true;
            else if (long.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out long gold) && gold > 0)
                _gold = gold;
            else if (_enchant && part is CrafterStones or CustomerStones)
                _crafterStones = part == CrafterStones;
            else if (_enchant && _itemId.Length == 0 && part.Length > 0 && !part.Contains('='))
                _itemId = part;
            else
                _parseError = _enchant
                    ? $"contract:propose:enchant: '{part}' is neither an item id, a gold amount, 'stones=crafter' nor 'expire'"
                    : $"contract:propose: '{part}' is neither a gold amount nor 'expire'";
        }
        if (_defId.Length == 0)
            _parseError = "contract:propose:<mold or recipe>";
    }

    public string Id => "contract";

    /// <summary>An enchant contract's proposal (<c>contract:propose:enchant...</c>), its item id (empty: any) and the stones' supplier.</summary>
    internal bool ProposesEnchant => _enchant;

    internal string EnchantItemId => _itemId;

    internal bool CrafterSuppliesStones => _crafterStones;

    /// <summary>The crafter's enchant answer of an enchant contract, for the logs and tests.</summary>
    public EnchantResult? Enchanted { get; private set; }

    public void Begin(BotStepContext context)
    {
        _phase = Phase.Idle;
        _pending = 0;
        _enchantPending = 0;
        _enchantItem = null;
        _crafterStone = null;
        _crafterStoneCount = 0;
        _label = _defId;
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
                context.Log($"contract {_contractId} proposed: {_label} for {result.Gold} gold");
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
                    context.Count(_expire || _crafterExpires ? "contract.expired" : "contract.completed");
                    _mailWaitUntilMs = context.NowMs + MailWaitMs;
                }
                // Its mail (the output, or the return) arrives a moment after the close committed.
                if (_mailBefore >= 0 && context.Mail != null && context.Mail.OpenCount <= _mailBefore && context.NowMs < _mailWaitUntilMs)
                    return BotStepStatus.Running;
                return BotStepStatus.Done;
        }

        if (_enchant)
            return ProposeEnchant(context, contracts, settings);
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
        var offerable = contents.Slots.Where(s => !s.Stolen && s.StolenMark == 0 && !s.Tainted && !s.NonTradable).ToList();
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

    /// <summary>An enchant contract's proposal (WP-M4-10 part C): the item and the stones as the terms want them, then the request.</summary>
    private BotStepStatus ProposeEnchant(BotStepContext context, Game.Gameplay.Entities.ClientContracts contracts, ContractSettings settings)
    {
        if (context.Enchant.Rules is not { } rules)
            return context.NotAvailable(Id, "no enchant tables");
        if (_itemId.Length > 0 && rules.Item(_itemId) == null)
            return context.NotAvailable(Id, $"no item '{_itemId}'");
        var contents = context.Inventory.Contents;
        if (contents == null)
            return BotStepStatus.Running;
        if (context.InCombat || context.View.Dead)
            return BotStepStatus.Running;
        var missing = TryEnchantSlots(rules, contents, _itemId, _crafterStones, _slots, out var item);
        if (missing != null)
        {
            string what = _itemId.Length > 0 ? _itemId : "an item of a job";
            return Soft(context, missing, missing == "contract.noItem"
                ? $"no {what} below +{EnchantLimits.MaxLevel} in the Safe bag to enchant (an equipped item is not offered)"
                : $"the Normal bag holds too few stones to enchant {item!.Id}");
        }
        long gold = _gold > 0 ? _gold : settings.MinGold;
        if ((context.Outposts.Gold ?? 0) < gold)
            return Soft(context, "contract.noGold", $"{gold} gold needed");
        _label = $"enchant {item!.Id} +{_slots[ContractRules.EnchantItemSlot].EnchantLevel}"
                 + (_crafterStones ? " (the crafter's stones)" : "");
        context.SendReliable(contracts.ProposeRequest(ForgeKind.Enchant, item.NetId, gold, settings.MinDeadlineHours, _slots, out _pending));
        _phase = Phase.Proposing;
        return BotStepStatus.Running;
    }

    /// <summary>
    /// The two slots of an enchant contract on the customer's side (<see cref="ContractRules.CheckEnchantTerms"/>): slot 0 the first
    /// instance in the Safe bag's equipment area (of <paramref name="itemId"/> when not empty) that the enchant rules take below +9,
    /// unreserved and tradable (R-5.4-03), slot 1 the stones of its tier: from one Normal bag stack holding enough of them, or
    /// named for the crafter with <paramref name="crafterStones"/>. Null when filled, else the counter of what is missing
    /// (<c>contract.noItem</c>, <c>contract.noStones</c>; <paramref name="item"/> is set for the second).
    /// </summary>
    internal static string? TryEnchantSlots(EnchantRules rules, InventoryContents contents, string itemId, bool crafterStones,
        List<ContractSlot> into, out ItemDef? item)
    {
        into.Clear();
        item = null;
        InventorySlotEntry? target = null;
        foreach (var container in contents.Containers ?? Array.Empty<InventoryContainerEntry>())
        {
            if (container.Code != rules.SafeCode)
                continue;
            foreach (var entry in container.Slots ?? Array.Empty<InventorySlotEntry>())
            {
                if (entry.InstanceId <= 0 || entry.Reserved || entry.Stolen || entry.StolenMark != 0 || entry.Tainted || entry.NonTradable
                    || rules.Item(entry.ItemNetId) is not { } candidate || (itemId.Length > 0 && candidate.Id != itemId))
                    continue;
                var station = rules.StationOf(rules.JobOf(candidate));
                if (station == NpcInteraction.None || rules.Check(candidate, station, _ => true, entry.Enchant) != EnchantReason.None
                    || !rules.TryMaterial(candidate, out _, out _))
                    continue;
                target = entry;
                item = candidate;
                break;
            }
        }
        if (target == null || item == null)
            return "contract.noItem";
        rules.TryMaterial(item, out var stone, out int count);
        into.Add(new ContractSlot
        {
            Supplier = ContractSupplier.Customer, Container = rules.SafeCode, Slot = target.Slot, ItemNetId = (ushort)item.NetId,
            Rarity = target.Rarity, Count = 1, InstanceId = target.InstanceId, EnchantLevel = target.Enchant,
        });
        if (crafterStones)
        {
            into.Add(new ContractSlot { Supplier = ContractSupplier.Crafter, ItemNetId = (ushort)stone.NetId, Count = (ushort)count });
            return null;
        }
        foreach (var entry in contents.Slots ?? Array.Empty<InventorySlotEntry>())
        {
            if (entry.ItemNetId != stone.NetId || entry.Count < count || entry.Reserved || entry.Stolen || entry.StolenMark != 0
                || entry.Tainted || entry.NonTradable)
                continue;
            into.Add(new ContractSlot
            {
                Supplier = ContractSupplier.Customer, Container = rules.NormalCode, Slot = entry.Slot, ItemNetId = (ushort)stone.NetId,
                Count = (ushort)count,
            });
            return null;
        }
        into.Clear();
        return "contract.noStones";
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
                if (entry.Kind == ForgeKind.Enchant && _expireAfterStart)
                    return context.Failed($"contract {_contractId}: contract:craft:expire starts the work of a forge contract, not an enchant one");
                if (entry.Kind == ForgeKind.Enchant)
                    return StartEnchant(context, entry);
                if (context.Forge.Rules is not { } rules || !rules.TryGet(entry.Kind, entry.DefNetId, out var definition))
                    return context.Failed($"contract {_contractId}: unknown definition {entry.Kind} {entry.DefNetId}");
                _goldBefore = context.Outposts.Gold ?? 0;
                _run = new ForgeRun("contract", _ => false) { AbandonAfterStart = _expireAfterStart };
                _run.Begin();
                _run.UseContract(_contractId, definition);
                _phase = Phase.Forging;
                return BotStepStatus.Running;
            case Phase.Forging:
                var status = _run!.Tick(context, ref command);
                if (status != BotStepStatus.Done)
                    return status;
                if (_expireAfterStart)
                {
                    context.Log($"contract {_contractId}: the work started, the session cancelled");
                    _expireAtMs = 0; // set once the own list shows the work started (ExpireStarted)
                    _waitUntilMs = context.NowMs + context.Behaviour.PairWaitMs;
                    _devSent = false;
                    _phase = Phase.Expiring;
                    return BotStepStatus.Running;
                }
                context.Count("contract.crafted");
                _waitUntilMs = context.NowMs + PaidWaitMs;
                _phase = Phase.Paid;
                return BotStepStatus.Running;
            case Phase.Enchanting:
                return Enchant(context, ref command);
            case Phase.Expiring:
                return ExpireStarted(context, contracts);
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

    /// <summary>
    /// An accepted enchant contract (WP-M4-10 part C): its item from the definition id, the stones the crafter brings when the
    /// terms' stone slot names the crafter (else the escrow holds the customer's), then <see cref="Enchant"/>.
    /// </summary>
    private BotStepStatus StartEnchant(BotStepContext context, ContractEntry entry)
    {
        if (context.Enchant.Rules is not { } rules)
            return context.NotAvailable(Id, "no enchant tables");
        if (rules.Item(entry.DefNetId) is not { } item || rules.StationOf(rules.JobOf(item)) == NpcInteraction.None)
            return context.Failed($"contract {_contractId}: {entry.DefNetId} is no enchantable item");
        _enchantItem = item;
        _crafterStone = null;
        _crafterStoneCount = 0;
        var slots = entry.Slots ?? Array.Empty<ContractSlot>();
        if (slots.Length > ContractRules.EnchantStonesSlot && slots[ContractRules.EnchantStonesSlot] is { Supplier: ContractSupplier.Crafter } stones)
        {
            _crafterStone = rules.Item(stones.ItemNetId);
            _crafterStoneCount = stones.Count;
            if (_crafterStone == null)
                return context.Failed($"contract {_contractId}: unknown stone {stones.ItemNetId}");
        }
        _goldBefore = context.Outposts.Gold ?? 0;
        _enchantPending = 0;
        _phase = Phase.Enchanting;
        return BotStepStatus.Running;
    }

    /// <summary>
    /// The one attempt of an enchant contract, as <see cref="EnchantStep"/> makes its own: out of combat and idle, at a station of
    /// the item's job, an <c>EnchantRequest</c> naming the contract, its answer after the commit; then the pay check.
    /// </summary>
    private BotStepStatus Enchant(BotStepContext context, ref BotCommand command)
    {
        var enchant = context.Enchant;
        var rules = enchant.Rules!;
        var item = _enchantItem!;
        if (_enchantPending != 0)
        {
            if (enchant.ResultFor(_enchantPending) is not { } result)
                return BotStepStatus.Running;
            _enchantPending = 0;
            if (result.Reason != EnchantReason.None)
            {
                context.Count("contract.enchantRefused." + result.Reason);
                return context.Failed($"contract {_contractId}: the enchant was refused: {result.Reason}");
            }
            Enchanted = result;
            string outcome = result.ToLevel > result.FromLevel ? "up" : result.ToLevel < result.FromLevel ? "down" : "same";
            context.Count("contract.enchanted");
            context.Count("contract.enchant." + outcome);
            context.Log($"contract {_contractId}: enchanted {item.Id} +{result.FromLevel} -> +{result.ToLevel}, {result.StonesUsed} stone(s)");
            _waitUntilMs = context.NowMs + PaidWaitMs;
            _phase = Phase.Paid;
            return BotStepStatus.Running;
        }
        if (_crafterStone != null && enchant.StonesHeld(context.Inventory.Contents, _crafterStone) < _crafterStoneCount)
            return Soft(context, "contract.noStones", $"needs {_crafterStoneCount} {_crafterStone.Id} for contract {_contractId}");
        if (context.InCombat || !context.View.ActionIdle || context.View.Dead)
            return BotStepStatus.Running; // enchanting happens out of combat (spec 5.3 M3 decision 7)
        var station = rules.StationOf(rules.JobOf(item));
        switch (context.ApproachNpc(npc => npc.Offers(station), ref command, out uint stationId, out _))
        {
            case NpcApproach.None:
                return Soft(context, "contract.noStation", $"no {station} station in reach for contract {_contractId}");
            case NpcApproach.Walking:
                return BotStepStatus.Running;
        }
        context.SendReliable(enchant.Request(stationId, 0, 0, 0, out _enchantPending, _contractId));
        return BotStepStatus.Running;
    }

    /// <summary>
    /// <c>contract:craft:expire</c> (QA M4 wave 2, D2): the work started and its session was cancelled; once the own contract list
    /// shows the work started (the world's flag), and <c>BotBehaviour.contractWorkSavedWaitMs</c> after it for that write's save (no
    /// message tells the client its commit), the crafter makes the contract due through the dev console (<c>/contracts expire</c>, a server with the dev tools on), and
    /// is done once the contract left its list and its gold fell by <c>ContractSettings.cancelCostPercent</c> of the contract gold
    /// (the expiry charge of a started contract, collected at once from a crafter online; "Yes, charge on expiry", user 2026-11-16).
    /// Counter: <c>contract.expiryCharged</c> (gold).
    /// </summary>
    private BotStepStatus ExpireStarted(BotStepContext context, Game.Gameplay.Entities.ClientContracts contracts)
    {
        long cost = ContractMath.CancelCost(_contractGold, ContractSettings.Find(context.Data.Tables)!.CancelCostPercent);
        if (!_devSent)
        {
            if (_expireAtMs == 0)
            {
                if (contracts.MineEntry(_contractId) is not { WorkStarted: true })
                    return context.NowMs < _waitUntilMs ? BotStepStatus.Running
                        : context.Failed($"contract {_contractId}: the own list never showed the work started");
                _expireAtMs = context.NowMs + context.Behaviour.ContractWorkSavedWaitMs;
                context.Log($"contract {_contractId}: work started in the own list; due in {context.Behaviour.ContractWorkSavedWaitMs} ms");
            }
            if (context.NowMs < _expireAtMs)
                return BotStepStatus.Running;
            _goldBefore = context.Outposts.Gold ?? 0;
            _devSent = context.SendReliable(ChatCodec.WriteSend(new ReliableMessageWriter(),
                new ChatSendMessage { Text = "/contracts expire" }).ToArray());
            context.Log("contract: > /contracts expire");
            _waitUntilMs = context.NowMs + context.Behaviour.PairWaitMs;
            return BotStepStatus.Running;
        }
        long lost = _goldBefore - (context.Outposts.Gold ?? 0);
        if (contracts.MineEntry(_contractId) == null && contracts.Mine != null && lost == cost)
        {
            context.Count("contract.expired");
            context.Count("contract.expiryCharged", lost);
            context.Log($"contract {_contractId} expired after its work started: charged {lost} gold ({_contractGold} x the cancel cost)");
            return BotStepStatus.Done;
        }
        return context.NowMs < _waitUntilMs ? BotStepStatus.Running
            : context.Failed($"contract {_contractId}: after its expiry the gold fell by {lost}, expected {cost}"
                             + (contracts.MineEntry(_contractId) != null ? " (still listed)" : ""));
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
