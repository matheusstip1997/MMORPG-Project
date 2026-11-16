using GameServer.Combat;
using GameServer.Contracts;
using GameServer.Entities;
using GameServer.Net;
using GameServer.Outposts;
using GameServer.Persistence;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Items;

namespace GameServer.World;

/// <summary>
/// Contracts and the portfolio in the world (spec 5.4, M4 decisions 10 and 11; WP-M4-10): <see cref="ContractService"/> holds the
/// open contracts, <see cref="PortfolioService"/> the portfolio writes. Phase 1 reads the requests (the <c>ContractSettings</c>
/// rate limit, spec 7.6): the board's page and an acceptance pass the contract board gate (the outpost pattern: the NPC in reach);
/// a proposal, a cancel, the own contracts and the portfolio work anywhere through the C window (M4 decision 10). Phase 7 runs the
/// expiry job; phase 10 sends the answers and the own contracts of whoever's changed. Without <see cref="EnableContracts"/> (a
/// world for tests, a config without the table, the forge, fees or mail) every request is refused <c>InteractionUnavailable</c>.
/// </summary>
public sealed partial class GameWorld
{
    private ContractService? _contracts;
    private PortfolioService? _portfolio;
    private ContractSender? _contractSender;

    /// <summary>The contracts (WP-M4-10); null until <see cref="EnableContracts"/>.</summary>
    public ContractService? Contracts => _contracts;

    /// <summary>The portfolios (WP-M4-10); null until <see cref="EnableContracts"/>.</summary>
    public PortfolioService? Portfolio => _portfolio;

    /// <summary>The world clock the contracts run on (UTC ms; the dev console moves deadlines to it).</summary>
    internal long ContractClockMs => _clock.UtcNowMs;

    /// <summary>
    /// Turns contracts on (after <see cref="EnableListings"/>, before the <c>SessionManager</c> is built, which wires their saves):
    /// needs the items, escrow, fees, mail and the forge. <paramref name="readPortfolio"/> reads a portfolio off the tick (null:
    /// views are refused). <paramref name="readCharge"/> reads an expiry's committed charge by its key off the tick (null: no
    /// expiry charges this run). The caller loads the open contracts (<see cref="ContractService.Load"/>) before the world starts.
    /// </summary>
    public void EnableContracts(ContractSettings settings, Func<long, Task<PortfolioRecord>>? readPortfolio,
        Func<string, Task<ContractChargeFound?>>? readCharge = null)
    {
        if (_inventory == null || _escrow == null || Fees == null || _mail == null || Economy == null || Outposts == null || _forge == null)
            return;
        _contractSender = new ContractSender(_transport);
        _portfolio = new PortfolioService(settings, _inventory, _clock, _events, readPortfolio) { Events = _events };
        _contracts = new ContractService(settings, _inventory, _escrow, Fees, _mail, _forge.Rules, _jobs, _clock, _events,
            Economy.Settings.MaxGold)
        {
            Answer = OnContractAnswer,
            Changed = MarkContractsChanged,
            AbortSession = AbortContractSession,
            RegionOf = id => _playersByCharacter.TryGetValue(id, out var p) ? p.RegionIndex : (byte)0,
            Portfolio = _portfolio,
            ReadCharge = readCharge,
        };
        _escrow.MailReleaseSettled += _contracts.OnMailReleaseSettled;
        _forge.Contracts = _contracts;
        Outposts.Register(NpcInteraction.Contracts, new ContractBoardInteraction(this));
    }

    /// <summary>Phase 1: a contract or portfolio request of <paramref name="player"/>; false when the request is not one.</summary>
    private bool HandleContractRequest(Player player, in ClientRequest request)
    {
        if (request.Kind is not (ReliableMessageId.ContractBoardRequest or ReliableMessageId.ContractRequest
            or ReliableMessageId.PortfolioRequest))
            return false;
        long now = _clock.UtcNowMs;
        if (_contracts == null || _portfolio == null || Outposts == null)
        {
            RefuseContract(player, request, ContractReason.InteractionUnavailable);
            return true;
        }
        var settings = _contracts.Settings;
        if (!player.ContractBucket.TryTake(now, settings.RequestBurst, settings.RequestsPerPeriod, settings.RequestPeriodMs))
        {
            RefuseContract(player, request, ContractReason.ContractRateLimited);
            return true;
        }
        switch (request.Body)
        {
            case ContractBoardRequest board:
                if (BoardGate(player, request.DropEntityId) is { } refusedBoard)
                    RefuseContract(player, request, refusedBoard);
                else
                    player.ContractMessages.Add(ReliableMessages.Write(ReliableMessageId.ContractBoard, BoardOf(player, board)));
                break;
            case ContractRequest { Action: ContractAction.Mine } mine:
                player.ContractMessages.Add(ReliableMessages.Write(ReliableMessageId.ContractList, ListOf(player, mine.RequestId)));
                break;
            case ContractRequest { Action: ContractAction.Propose } propose:
                AnswerContract(player, _contracts.Propose(ContractAskOf(player, propose.RequestId, "contract", now), propose.Kind,
                    propose.DefNetId, propose.Gold, propose.DeadlineHours, propose.Slots));
                break;
            case ContractRequest { Action: ContractAction.Accept } accept:
                if (BoardGate(player, request.DropEntityId) is { } refusedAccept)
                    RefuseContract(player, request, refusedAccept);
                else
                    AnswerContract(player, _contracts.Accept(ContractAskOf(player, accept.RequestId, "contractAccept", now), accept.ContractId));
                break;
            case ContractRequest cancel:
                AnswerContract(player, _contracts.Cancel(ContractAskOf(player, cancel.RequestId, "contractCancel", now), cancel.ContractId));
                break;
            case PortfolioRequest portfolio:
                HandlePortfolio(player, portfolio);
                break;
        }
        return true;
    }

    /// <summary>The contract board gate: a visible board in reach (null), or the refusal.</summary>
    private ContractReason? BoardGate(Player player, uint npcEntityId) =>
        Outposts!.CheckReach(player, npcEntityId, NpcInteraction.Contracts, out _) switch
        {
            OutpostReason.None => null,
            OutpostReason.NpcNotFound => ContractReason.NpcNotFound,
            OutpostReason.OutOfRange => ContractReason.OutOfRange,
            _ => ContractReason.InteractionUnavailable,
        };

    /// <summary>The ask of a request: who, which connection, the region (fees) and the operation key (one per run, entity, connection, request).</summary>
    private ContractAsk ContractAskOf(Player player, uint requestId, string op, long now)
    {
        // A contract operation never interrupts combat or an action under way (M4 plan risk "souls-like feel").
        bool blocked = _inventory == null || CombatState.IsInCombat(player.LastCombatAtMs, now, _inventory.Rules.CombatStateMs)
                       || player.Actions.IsBusy;
        return new ContractAsk(player.CharacterId, player.Connection, requestId, player.RegionIndex, blocked,
            $"{op}:{_runId}:{player.Id}:{player.Connection}:{requestId}");
    }

    private void HandlePortfolio(Player player, PortfolioRequest request)
    {
        var portfolio = _portfolio!;
        var own = new PortfolioAsk(player.CharacterId, player.Connection, request.RequestId, player.CharacterId, true);
        ContractReason reason;
        switch (request.Action)
        {
            case PortfolioAction.Pin:
                reason = portfolio.Pin(player.CharacterId, new InventoryLocation(request.Container, request.Slot), request.InstanceId,
                    request.ShowcaseSlot, own);
                break;
            case PortfolioAction.Unpin:
                reason = portfolio.Unpin(player.CharacterId, request.ShowcaseSlot, own);
                break;
            case PortfolioAction.SetHidden:
                reason = portfolio.SetHidden(player.CharacterId, request.Hidden, own);
                break;
            default:
                long target = player.CharacterId;
                if (request.ContractId != 0)
                {
                    // The crafter of one of the player's own contracts (the customer reads who works for it).
                    target = _contracts!.Find(request.ContractId) is { } row && row.CustomerId == player.CharacterId ? row.CrafterId : 0;
                }
                else if (request.TargetEntityId != 0)
                {
                    // A player in view (rule 9: only what the player may see; at the board or anywhere).
                    target = _findEntity(request.TargetEntityId) is Player other && (other == player || player.Interest.Contains(other))
                        ? other.CharacterId : 0;
                }
                if (target == 0)
                {
                    reason = ContractReason.PortfolioNotFound;
                    break;
                }
                reason = portfolio.View(own with { TargetId = target, Own = target == player.CharacterId })
                    ? ContractReason.None : ContractReason.PortfolioNotFound;
                break;
        }
        if (reason != ContractReason.None)
            player.ContractMessages.Add(ReliableMessages.Write(ReliableMessageId.PortfolioState,
                new PortfolioState { RequestId = request.RequestId, Reason = reason }));
    }

    /// <summary>Phase 1 (after the commands): portfolio reads that came back, to the connections that asked.</summary>
    private void DrainPortfolioViews()
    {
        // No allocation per tick (the lambda's closure lives in the method that sends, entered only with an answer).
        while (_portfolio != null && _portfolio.TryTakeAnswer(out var answer))
            SendPortfolio(answer);
    }

    private void SendPortfolio(in PortfolioAnswer answer)
    {
        var jobs = _jobs?.Rules;
        {
            var ask = answer.Ask;
            if (!_playersByCharacter.TryGetValue(ask.ViewerId, out var player) || player.Connection != ask.Connection)
                return;
            player.ContractMessages.Add(ReliableMessages.Write(ReliableMessageId.PortfolioState,
                _portfolio!.StateOf(ask, answer.Record, jobId => jobs?.Find(jobId)?.NetId ?? 0)));
        }
    }

    private ContractBoard BoardOf(Player player, ContractBoardRequest request)
    {
        var contracts = _contracts!;
        var jobs = _jobs?.Rules;
        int page = contracts.Settings.BoardPageSize;
        var rows = contracts.Board(request.JobNetId, request.Kind, request.AfterContractId, page + 1, jobId => jobs?.Find(jobId)?.NetId ?? 0);
        return new ContractBoard
        {
            RequestId = request.RequestId,
            HasMore = rows.Count > page,
            Rows = rows.Take(page).Select(r => contracts.Entry(r, player.CharacterId)).ToArray(),
        };
    }

    private ContractList ListOf(Player player, uint requestId)
    {
        var contracts = _contracts!;
        var rows = contracts.Mine(player.CharacterId);
        return new ContractList
        {
            RequestId = requestId,
            Badge = (ushort)Math.Min(ushort.MaxValue, rows.Count),
            Rows = rows.Take(ReliableMessages.MaxMyContracts).Select(r => contracts.Entry(r, player.CharacterId)).ToArray(),
        };
    }

    private static void AnswerContract(Player player, ContractResult? answer)
    {
        if (answer != null)
            player.ContractMessages.Add(ReliableMessages.Write(ReliableMessageId.ContractResult, answer));
    }

    /// <summary>An operation committed (or a release settled): the answer goes to the connection that asked, if it is still bound.</summary>
    private void OnContractAnswer(ContractAsk ask, ContractResult answer)
    {
        if (_playersByCharacter.TryGetValue(ask.CharacterId, out var player) && player.Connection == ask.Connection)
            player.ContractMessages.Add(ReliableMessages.Write(ReliableMessageId.ContractResult, answer));
    }

    private void MarkContractsChanged(long characterId)
    {
        if (_playersByCharacter.TryGetValue(characterId, out var player))
            player.ContractListDirty = true;
    }

    /// <summary>The deadline of a contract passed while its crafter forges on it: the session ends (its inputs released), the client hears it.</summary>
    private void AbortContractSession(long crafterId)
    {
        if (_forge == null || !_forge.Abort(crafterId) || !_playersByCharacter.TryGetValue(crafterId, out var player))
            return;
        var state = _forge.State(crafterId, 0, ForgeReason.ContractExpired);
        state.Phase = ForgePhase.Cancelled;
        player.Forge.States.Add(state);
    }

    private static void RefuseContract(Player player, in ClientRequest request, ContractReason reason)
    {
        switch (request.Kind)
        {
            case ReliableMessageId.ContractBoardRequest:
                player.ContractMessages.Add(ReliableMessages.Write(ReliableMessageId.ContractBoard,
                    new ContractBoard { RequestId = request.RequestId, Reason = reason }));
                break;
            case ReliableMessageId.PortfolioRequest:
                player.ContractMessages.Add(ReliableMessages.Write(ReliableMessageId.PortfolioState,
                    new PortfolioState { RequestId = request.RequestId, Reason = reason }));
                break;
            default:
                var action = request.Body is ContractRequest asked ? asked.Action : ContractAction.None;
                if (action == ContractAction.Mine)
                    player.ContractMessages.Add(ReliableMessages.Write(ReliableMessageId.ContractList,
                        new ContractList { RequestId = request.RequestId, Reason = reason }));
                else
                    player.ContractMessages.Add(ReliableMessages.Write(ReliableMessageId.ContractResult,
                        new ContractResult { RequestId = request.RequestId, Action = action, Reason = reason }));
                break;
        }
    }

    /// <summary>Phase 7: the expiry job (proposals past their time, contracts past their deadline: back to the customer by mail).</summary>
    private void ExpireContracts() => _contracts?.ExpireDue(_clock.UtcNowMs);

    /// <summary>Phase 10: the player's contract answers, then its own contracts when one of them changed.</summary>
    private void SendContracts(Player player)
    {
        if (_contractSender == null || _contracts == null)
            return;
        if (player.ContractListDirty)
        {
            player.ContractListDirty = false;
            player.ContractMessages.Add(ReliableMessages.Write(ReliableMessageId.ContractList, ListOf(player, 0)));
        }
        _contractSender.Send(player);
    }

    /// <summary>The contract board (WP-M4-10): its yes opens the board window, with the player's own contracts pushed along.</summary>
    private sealed class ContractBoardInteraction(GameWorld world) : IOutpostInteractionHandler
    {
        public OutpostReason? Interact(Player player, Npc npc, uint requestId)
        {
            if (world._contracts == null)
                return OutpostReason.InteractionUnavailable;
            player.ContractListDirty = true;
            return OutpostReason.None;
        }
    }
}
