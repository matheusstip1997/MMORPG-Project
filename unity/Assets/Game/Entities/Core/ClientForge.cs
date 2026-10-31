#nullable enable
using System;
using System.Collections.Generic;
using Shared.Protocol;
using Shared.Simulation.Crafting;

namespace Game.Gameplay.Entities
{
    /// <summary>
    /// The client's side of the forge (spec 5.3, 9.13; WP-M3-6), engine-free so the bots use it too: the session as the server
    /// last sent it (<see cref="ForgeState"/>), the last finish answer (<see cref="ForgeResult"/>), the forge requests with
    /// their ids (one counter per connection), and the input picker the window and the bots share. The server checks every
    /// request again; the client never decides a quality, a rarity or an item.
    /// </summary>
    public sealed class ClientForge
    {
        /// <summary>The forge tables (from the config the client loaded); without them nothing can be picked.</summary>
        public ForgeRules? Rules { get; set; }

        /// <summary>The last state (null before the first); a newer version replaces an older one.</summary>
        public ForgeState? State { get; private set; }

        public ForgeResult? LastResult { get; private set; }

        /// <summary>Forge requests sent on this connection; the next one gets this plus one as its id.</summary>
        public uint RequestsSent { get; private set; }

        /// <summary>Counts the states and results taken and the resets (the UI redraws on a change).</summary>
        public int Revision { get; private set; }

        /// <summary>A session runs or waits for its save (the window shows its actions and preview).</summary>
        public bool InSession => State != null && (State.Phase == ForgePhase.Running || State.Phase == ForgePhase.Finishing);

        /// <summary>
        /// Takes the forge messages of the reliable channel; false for any other message. <paramref name="log"/> says what
        /// happened in words for the dev log.
        /// </summary>
        public bool Apply(ReadOnlySpan<byte> message, out string log)
        {
            log = string.Empty;
            switch (ReliableMessages.PeekId(message))
            {
                case ReliableMessageId.ForgeState:
                    if (ReliableMessages.TryRead(message, out ForgeState state) && (State == null || state.Version > State.Version))
                    {
                        State = state;
                        Revision++;
                        log = $"forge {state.Phase} ({state.Kind} {state.DefNetId}), request {state.RequestId}: {state.Reason}";
                    }
                    return true;
                case ReliableMessageId.ForgeResult:
                    if (ReliableMessages.TryRead(message, out ForgeResult result))
                    {
                        LastResult = result;
                        Revision++;
                        log = $"forge result {result.RequestId}: {result.Reason}, item {result.OutputItemNetId} x{result.Count}, "
                              + $"quality {result.Quality}, rarity {result.Rarity}, success {result.Success}";
                    }
                    return true;
                default:
                    return false;
            }
        }

        public byte[] Start(uint stationEntityId, ForgeKind kind, int defNetId, IReadOnlyList<ForgeInput> inputs, out uint requestId)
        {
            requestId = ++RequestsSent;
            var array = new ForgeInput[inputs.Count];
            for (int i = 0; i < array.Length; i++)
                array[i] = inputs[i];
            return ReliableMessages.Write(ReliableMessageId.ForgeStart, new ForgeStart
            {
                RequestId = requestId, StationEntityId = stationEntityId, Kind = kind, DefNetId = (ushort)defNetId, Inputs = array,
            });
        }

        public byte[] Action(int actionNetId, out uint requestId)
        {
            requestId = ++RequestsSent;
            return ReliableMessages.Write(ReliableMessageId.ForgeAction, new ForgeAction { RequestId = requestId, ActionNetId = (byte)actionNetId });
        }

        public byte[] Finish(out uint requestId)
        {
            requestId = ++RequestsSent;
            return ReliableMessages.Write(ReliableMessageId.ForgeFinish, new ForgeFinish { RequestId = requestId });
        }

        public byte[] Cancel(out uint requestId)
        {
            requestId = ++RequestsSent;
            return ReliableMessages.Write(ReliableMessageId.ForgeCancel, new ForgeCancel { RequestId = requestId });
        }

        /// <summary>The state answering <paramref name="requestId"/>, or null when it has not come yet.</summary>
        public ForgeState? StateFor(uint requestId) => State != null && State.RequestId == requestId ? State : null;

        /// <summary>The finish answer for <paramref name="requestId"/>, or null when it has not come yet.</summary>
        public ForgeResult? ResultFor(uint requestId) => LastResult != null && LastResult.RequestId == requestId ? LastResult : null;

        /// <summary>
        /// Picks a Normal bag stack for every input of <paramref name="definition"/>: per input, the first unreserved slot holding a
        /// material it takes with enough units left after the earlier inputs' picks. False (and <paramref name="inputs"/> partly
        /// filled) when an input finds none. <paramref name="normalBag"/> is the Normal bag's container code.
        /// </summary>
        public bool TryPickInputs(ForgeDefinition definition, IReadOnlyList<InventorySlotEntry> bag, byte normalBag, List<ForgeInput> inputs)
        {
            inputs.Clear();
            if (Rules == null)
                return false;
            var taken = new Dictionary<int, int>();
            foreach (var spec in definition.Inputs)
            {
                InventorySlotEntry? chosen = null;
                foreach (var entry in bag)
                {
                    if (entry.Count <= 0 || entry.Reserved || Rules.Item(entry.ItemNetId) is not { } item
                        || !spec.Takes(item))
                        continue;
                    taken.TryGetValue(entry.Slot, out int used);
                    if (entry.Count - used < spec.Count)
                        continue;
                    chosen = entry;
                    break;
                }
                if (chosen == null)
                    return false;
                taken.TryGetValue(chosen.Slot, out int already);
                taken[chosen.Slot] = already + spec.Count;
                inputs.Add(new ForgeInput { Container = normalBag, Slot = chosen.Slot });
            }
            return true;
        }

        /// <summary>The connection ended: the session shown is gone and the next connection numbers its requests from 1.</summary>
        public void Reset()
        {
            State = null;
            LastResult = null;
            RequestsSent = 0;
            Revision++;
        }
    }
}
