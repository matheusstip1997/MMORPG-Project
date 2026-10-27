#nullable enable
using System;
using Shared.Protocol;

namespace Game.Gameplay.Entities
{
    /// <summary>
    /// The client's view of its own level, exp, rank and skill points (spec 3.6, 9.4; WP-M2-3): the last
    /// <see cref="ProgressionState"/> the server sent. Display only (spec 9.1): the server decides every number. Engine-free,
    /// so the bots (ClientCore) read the level the same way.
    /// </summary>
    public sealed class ClientProgression
    {
        /// <summary>The last state; null before the first message and after a reset.</summary>
        public ProgressionState? State { get; private set; }

        /// <summary>
        /// Takes a <see cref="ProgressionState"/> of the reliable channel; false for any other message. <paramref name="log"/>
        /// says what happened in words for the dev log.
        /// </summary>
        public bool Apply(ReadOnlySpan<byte> message, out string log)
        {
            log = string.Empty;
            if (ReliableMessages.PeekId(message) != ReliableMessageId.ProgressionState)
                return false;
            if (!ReliableMessages.TryRead(message, out ProgressionState state))
            {
                log = "dropped an unreadable progression message";
                return true;
            }
            State = state;
            log = $"level {state.Level} ({state.Exp}/{state.ExpToNext} exp), rank {state.Rank}, {state.SkillPoints} skill points"
                  + (state.Cause == ProgressionCause.Initial || state.Cause == ProgressionCause.Exp ? string.Empty : $": {state.Cause}");
            return true;
        }

        /// <summary>A new connection starts over (the server sends the whole state again).</summary>
        public void Reset() => State = null;
    }
}
