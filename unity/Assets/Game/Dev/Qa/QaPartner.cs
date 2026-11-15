#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Gameplay.Entities;
using Shared.Data.Tables;

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

        /// <summary>
        /// Whether the bot's answer to <paramref name="request"/> ends a wait for the player's own condition early: only an answer
        /// that is not done (failed, timed out, cancelled) does. A done script can answer a frame before the player's own state
        /// shows its effect (<c>party-up</c>: the bot's party state before the player's), so after "done" the wait goes on to its
        /// own timeout (PR #254 review 2).
        /// </summary>
        public bool EndsWait(int request, out string text)
        {
            return TryResult(request, out string outcome, out text) && outcome != PairScriptResult.Done;
        }

        /// <summary>Whether the bot answered every request in <paramref name="requests"/> (those one cancel gave up, PR #254 review 6).</summary>
        public bool AllAnswered(IReadOnlyList<int> requests)
        {
            foreach (int n in requests)
            {
                if (!Link.TryReadResult(n, out _))
                    return false;
            }
            return true;
        }

        /// <summary>The partner script of the smoke's <c>trade-item</c> step: <c>qa-partner-trade-item</c>.</summary>
        public const string TradeItemScript = BehaviourId + "-trade-item";

        /// <summary>
        /// The gold the player pays in <c>trade-item</c>, read from the partner's script row <see cref="TradeItemScript"/>: the
        /// "for" side of its first <c>trade:&lt;give&gt;:gold=&lt;n&gt;</c> entry, so the price is held once (PR #254 review 7).
        /// Throws <see cref="QaFailure"/> when the row or such an entry is missing.
        /// </summary>
        public static long TradeGold(IEnumerable<BotBehaviour> rows)
        {
            var row = rows.FirstOrDefault(b => b.Id == TradeItemScript)
                      ?? throw new QaFailure($"no BotBehaviour row {TradeItemScript}");
            foreach (string entry in row.LoopSteps)
            {
                string[] parts = entry.Split(':');
                if (parts.Length == 3 && parts[0] == "trade" && parts[2].StartsWith(GoldPrefix, StringComparison.Ordinal)
                    && long.TryParse(parts[2].Substring(GoldPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out long gold)
                    && gold > 0)
                    return gold;
            }
            throw new QaFailure($"the row {TradeItemScript} has no trade:<give>:gold=<n> entry: {string.Join(", ", row.LoopSteps)}");
        }

        private const string GoldPrefix = "gold=";

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
