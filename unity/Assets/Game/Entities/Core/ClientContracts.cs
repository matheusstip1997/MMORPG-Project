#nullable enable
using System;
using System.Collections.Generic;
using Shared.Protocol;

namespace Game.Gameplay.Entities
{
    /// <summary>
    /// The client's side of contracts and the portfolio (spec 5.4, 9.13; M4 decisions 10 and 11; WP-M4-10), engine-free so the bots
    /// use it too: the requests with their ids (one counter per connection, shared by every contract and portfolio request), the
    /// last board page (<see cref="ContractBoard"/>), the player's own contracts and the C badge (<see cref="ContractList"/>), the
    /// answers to propose, accept and cancel (<see cref="ContractResult"/>, kept by request id) and the last portfolio read
    /// (<see cref="PortfolioState"/>). The server owns every contract; the client only asks. The board's page and an acceptance
    /// name the contract board (<c>npcEntityId</c>); the rest works anywhere.
    /// </summary>
    public sealed class ClientContracts
    {
        private readonly Dictionary<uint, ContractResult> _results = new Dictionary<uint, ContractResult>();
        private readonly Dictionary<uint, PortfolioState> _portfolios = new Dictionary<uint, PortfolioState>();

        public ContractBoard? Board { get; private set; }

        /// <summary>The player's own open contracts (pushed at enter world and whenever one changes).</summary>
        public ContractList? Mine { get; private set; }

        public ContractResult? LastResult { get; private set; }

        public PortfolioState? LastPortfolio { get; private set; }

        /// <summary>Contract and portfolio requests sent on this connection; the next one gets this plus one as its id.</summary>
        public uint RequestsSent { get; private set; }

        /// <summary>Counts the messages taken and the resets (the UI redraws on a change).</summary>
        public int Revision { get; private set; }

        /// <summary>The C button's count (open contracts on either side), 0 before the first list.</summary>
        public int Badge => Mine?.Badge ?? 0;

        /// <summary>Takes the contract messages of the reliable channel; false for any other message.</summary>
        public bool Apply(ReadOnlySpan<byte> message, out string log)
        {
            log = string.Empty;
            switch (ReliableMessages.PeekId(message))
            {
                case ReliableMessageId.ContractBoard:
                    if (ReliableMessages.TryRead(message, out ContractBoard board))
                    {
                        Board = board;
                        Revision++;
                        log = $"contract board {board.RequestId}: {board.Reason}, {board.Rows.Length} rows, more {board.HasMore}";
                    }
                    return true;
                case ReliableMessageId.ContractList:
                    if (ReliableMessages.TryRead(message, out ContractList list))
                    {
                        Mine = list;
                        Revision++;
                        log = $"contracts {list.RequestId}: {list.Reason}, {list.Rows.Length} rows, badge {list.Badge}";
                    }
                    return true;
                case ReliableMessageId.ContractResult:
                    if (ReliableMessages.TryRead(message, out ContractResult result))
                    {
                        LastResult = result;
                        _results[result.RequestId] = result;
                        Revision++;
                        log = $"contract {result.Action} {result.RequestId}: {result.Reason}, contract {result.ContractId}, gold {result.Gold}, "
                              + $"fee {result.Fee}, cost {result.Cost}";
                    }
                    return true;
                case ReliableMessageId.PortfolioState:
                    if (ReliableMessages.TryRead(message, out PortfolioState portfolio))
                    {
                        LastPortfolio = portfolio;
                        _portfolios[portfolio.RequestId] = portfolio;
                        Revision++;
                        log = $"portfolio {portfolio.RequestId}: {portfolio.Reason}, own {portfolio.Own}, hidden {portfolio.Hidden}, "
                              + $"{portfolio.Counters.Length} counters, {portfolio.Showcase.Length} pinned";
                    }
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>One board page at the board <paramref name="npcEntityId"/>, after <paramref name="afterContractId"/> (0: the first).</summary>
        public byte[] BoardRequest(uint npcEntityId, byte jobNetId, ForgeKind kind, long afterContractId, out uint requestId)
        {
            requestId = ++RequestsSent;
            return ReliableMessages.Write(ReliableMessageId.ContractBoardRequest, new ContractBoardRequest
            {
                RequestId = requestId, NpcEntityId = npcEntityId, JobNetId = jobNetId, Kind = kind, AfterContractId = afterContractId,
            });
        }

        /// <summary>A proposal (anywhere): the definition, the gold, the deadline in hours and one slot per input in order.</summary>
        public byte[] ProposeRequest(ForgeKind kind, int defNetId, long gold, int deadlineHours, IReadOnlyList<ContractSlot> slots,
            out uint requestId)
        {
            requestId = ++RequestsSent;
            var copy = new ContractSlot[slots.Count];
            for (int i = 0; i < copy.Length; i++)
                copy[i] = slots[i];
            return ReliableMessages.Write(ReliableMessageId.ContractRequest, new ContractRequest
            {
                RequestId = requestId, Action = ContractAction.Propose, Kind = kind, DefNetId = (ushort)Math.Max(0, Math.Min(ushort.MaxValue, defNetId)),
                Gold = gold, DeadlineHours = (byte)Math.Max(0, Math.Min(byte.MaxValue, deadlineHours)), Slots = copy,
            });
        }

        /// <summary>Accepts <paramref name="contractId"/> at the board <paramref name="npcEntityId"/>.</summary>
        public byte[] AcceptRequest(uint npcEntityId, long contractId, out uint requestId)
        {
            requestId = ++RequestsSent;
            return ReliableMessages.Write(ReliableMessageId.ContractRequest, new ContractRequest
            {
                RequestId = requestId, NpcEntityId = npcEntityId, Action = ContractAction.Accept, ContractId = contractId,
            });
        }

        /// <summary>Cancels <paramref name="contractId"/> (as its customer or its crafter; anywhere).</summary>
        public byte[] CancelRequest(long contractId, out uint requestId)
        {
            requestId = ++RequestsSent;
            return ReliableMessages.Write(ReliableMessageId.ContractRequest, new ContractRequest
            {
                RequestId = requestId, Action = ContractAction.Cancel, ContractId = contractId,
            });
        }

        /// <summary>The player's own contracts (answered by a <see cref="ContractList"/>).</summary>
        public byte[] MineRequest(out uint requestId)
        {
            requestId = ++RequestsSent;
            return ReliableMessages.Write(ReliableMessageId.ContractRequest, new ContractRequest { RequestId = requestId, Action = ContractAction.Mine });
        }

        /// <summary>A portfolio request (see <see cref="PortfolioRequest"/>); the action's fields are set on <paramref name="request"/>.</summary>
        public byte[] PortfolioRequest(PortfolioRequest request, out uint requestId)
        {
            requestId = ++RequestsSent;
            request.RequestId = requestId;
            return ReliableMessages.Write(ReliableMessageId.PortfolioRequest, request);
        }

        /// <summary>The board page answering <paramref name="requestId"/>, or null when it has not come yet.</summary>
        public ContractBoard? BoardFor(uint requestId) => Board != null && Board.RequestId == requestId ? Board : null;

        /// <summary>The own contracts answering <paramref name="requestId"/> (0: pushed), or null.</summary>
        public ContractList? MineFor(uint requestId) => Mine != null && Mine.RequestId == requestId ? Mine : null;

        /// <summary>The answer to a propose, accept or cancel with <paramref name="requestId"/>, or null when it has not come yet.</summary>
        public ContractResult? ResultFor(uint requestId) => _results.TryGetValue(requestId, out var result) ? result : null;

        /// <summary>The portfolio answering <paramref name="requestId"/>, or null when it has not come yet.</summary>
        public PortfolioState? PortfolioFor(uint requestId) => _portfolios.TryGetValue(requestId, out var state) ? state : null;

        /// <summary>One of the player's own contracts as last listed, or null.</summary>
        public ContractEntry? MineEntry(long contractId)
        {
            if (Mine == null)
                return null;
            foreach (var row in Mine.Rows)
            {
                if (row.ContractId == contractId)
                    return row;
            }
            return null;
        }

        /// <summary>A new connection: its request ids start again.</summary>
        public void Reset()
        {
            Board = null;
            Mine = null;
            LastResult = null;
            LastPortfolio = null;
            _results.Clear();
            _portfolios.Clear();
            RequestsSent = 0;
            Revision++;
        }
    }
}
