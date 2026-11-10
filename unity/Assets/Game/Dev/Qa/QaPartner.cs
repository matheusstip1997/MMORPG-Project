#nullable enable
using System;
using Game.Gameplay.Entities;

namespace Game.Dev.Qa
{
    /// <summary>
    /// The smoke's side of the QA partner bot (WP-M4-12, spec 11.5; <c>tools/qa/Run-QaSmoke.ps1 -Partner</c>, README
    /// "Partner"): the second player of the pair steps is a BotClient bot (<c>BotClient run --partner-dir</c>, behaviour
    /// <c>qa-partner</c>) on the file pair board the harness names in <see cref="DirectoryVariable"/>. This side is the first
    /// of the pair (<see cref="FilePairBoard.PlayerSide"/>). A pair step posts the player's name and position, asks the bot to
    /// play its partner script (<see cref="PairLink.Request"/>: the bot plays the row <c>qa-partner-&lt;script&gt;</c> and answers
    /// with <see cref="PairScriptResult"/>'s text), meets it at named points (<see cref="PairLink.Arrive"/>,
    /// <see cref="PairLink.Check"/>), and reads the game's outcome from the server's messages, never from the board. Without the
    /// variable there is no partner and a pair step is n/a with <see cref="NeededReason"/>. Engine free: testable in EditMode.
    /// </summary>
    public sealed class QaPartner
    {
        /// <summary>The folder of the pair board, set by the harness when <c>-Partner</c> is on and the bot is in the world.</summary>
        public const string DirectoryVariable = "MMORPG_QA_PARTNER_DIR";

        /// <summary>Why a pair step is n/a without the partner bot.</summary>
        public const string NeededReason = "partner needed (tools/qa/Run-QaSmoke.ps1 -Partner)";

        /// <summary>The <c>BotBehaviour</c> row the partner bot plays (its <c>pairReachCm</c> is the meeting distance).</summary>
        public const string BehaviourId = "qa-partner";

        /// <param name="refreshIntervalMs">How often the bot's log is read at most (<see cref="FilePairBoard.DefaultRefreshIntervalMs"/>; 0 in tests).</param>
        public QaPartner(string directory, int refreshIntervalMs = FilePairBoard.DefaultRefreshIntervalMs)
        {
            Directory = directory;
            Link = new PairLink(new FilePairBoard(directory, FilePairBoard.PlayerSide, FilePairBoard.BotSide, refreshIntervalMs), isFirst: true);
        }

        /// <summary>The board's folder (<c>player.log</c> this side, <c>bot.log</c> the bot's).</summary>
        public string Directory { get; }

        public PairLink Link { get; }

        /// <summary>The partner from <see cref="DirectoryVariable"/>; null when the harness runs without <c>-Partner</c>.</summary>
        public static QaPartner? FromEnvironment()
        {
            string? directory = Environment.GetEnvironmentVariable(DirectoryVariable);
            return string.IsNullOrEmpty(directory) ? null : new QaPartner(directory!);
        }

        /// <summary>The bot's answer to request <paramref name="request"/> as its outcome word and the whole text; false before it answered.</summary>
        public bool TryResult(int request, out string outcome, out string text)
        {
            outcome = string.Empty;
            if (!Link.TryReadResult(request, out text))
                return false;
            outcome = PairScriptResult.Outcome(text);
            return true;
        }

        /// <summary>The smoke ended: the bot's serve step ends too (the bot logs out and prints its outcomes).</summary>
        public void Finish() => Link.SayGone();

        /// <summary>
        /// The n/a of a pair step whose feature has not landed: "partner needed" without the partner bot, else "not yet" naming
        /// the WP. Always throws <see cref="QaNotApplicable"/>.
        /// </summary>
        public static void NotYet(QaPartner? partner, string why)
        {
            if (partner == null)
                throw new QaNotApplicable(NeededReason);
            throw new QaNotApplicable("not yet: " + why);
        }
    }
}
