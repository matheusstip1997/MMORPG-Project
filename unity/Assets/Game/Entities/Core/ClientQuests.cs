#nullable enable
using System;
using Shared.Protocol;

namespace Game.Gameplay.Entities
{
    /// <summary>
    /// The client's side of outpost quests and runs (spec 5.5, 9.3, 9.13; WP-M2-6), engine-free so the bots use it too: the last
    /// quest board the server sent, the character's run as the server holds it, the last answer to a run request, and the run
    /// requests with their ids (one counter per connection: the server scopes them by connection). The server checks
    /// everything again; the client never decides a step's progress or a reward.
    /// </summary>
    public sealed class ClientQuests
    {
        /// <summary>The last board (null before the first); an accept names its epoch.</summary>
        public QuestBoard? Board { get; private set; }

        /// <summary>The character's run (state None: no run); null before the server sent it.</summary>
        public RunStatus? Run { get; private set; }

        public QuestRunResult? LastResult { get; private set; }

        /// <summary>Run requests sent on this connection; the next one gets this plus one as its id.</summary>
        public uint RequestsSent { get; private set; }

        /// <summary>
        /// Takes the quest messages of the reliable channel; false for any other message. <paramref name="log"/> says what
        /// happened in words for the dev log.
        /// </summary>
        public bool Apply(ReadOnlySpan<byte> message, out string log)
        {
            log = string.Empty;
            switch (ReliableMessages.PeekId(message))
            {
                case ReliableMessageId.QuestBoard:
                    if (ReliableMessages.TryRead(message, out QuestBoard board))
                    {
                        Board = board;
                        log = $"quest board of npc {board.NpcEntityId}: epoch {board.Epoch}, {board.Offers.Length} offers";
                    }
                    return true;
                case ReliableMessageId.QuestRunResult:
                    if (ReliableMessages.TryRead(message, out QuestRunResult result))
                    {
                        LastResult = result;
                        log = $"run {result.Action} {result.RequestId}: {result.Reason} (step {result.StepIndex}, {result.Count} units, {result.Exp} exp)";
                    }
                    return true;
                case ReliableMessageId.RunStatus:
                    if (ReliableMessages.TryRead(message, out RunStatus status))
                    {
                        Run = status;
                        log = $"run {status.State}: {status.Steps.Length} steps, claim {status.ClaimExp} exp";
                    }
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>A run request at an outpost NPC: accept (board epoch and offer slots in run order), deliver, forfeit or claim.</summary>
        public byte[] RunRequest(uint npcEntityId, QuestRunAction action, uint epoch, byte stepIndex, byte[] slots, out uint requestId)
        {
            requestId = ++RequestsSent;
            return ReliableMessages.Write(ReliableMessageId.QuestRunRequest, new QuestRunRequest
            {
                RequestId = requestId, NpcEntityId = npcEntityId, Action = action, Epoch = epoch, StepIndex = stepIndex, Slots = slots,
            });
        }

        /// <summary>The connection ended: the board and run shown are stale and the next connection numbers its requests from 1.</summary>
        public void Reset()
        {
            Board = null;
            Run = null;
            LastResult = null;
            RequestsSent = 0;
        }
    }
}
