#nullable enable
using System.Globalization;
using System.IO;
using System.Linq;
using Game.Gameplay.Entities;
using NUnit.Framework;
using Shared.Data.Tables;

namespace Game.Dev.Qa.EditorTests
{
    /// <summary>The smoke's side of the QA partner bot (WP-M4-12): the file pair board as the BotClient's partner reads it.</summary>
    public sealed class QaPartnerTests
    {
        private string _dir = "";

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "qa-partner-" + System.Guid.NewGuid().ToString("N"));
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, true);
        }

        [Test]
        public void A_request_reaches_the_bot_and_its_answer_comes_back()
        {
            var partner = new QaPartner(_dir, refreshIntervalMs: 0);
            var bot = new PairLink(new FilePairBoard(_dir, FilePairBoard.BotSide, FilePairBoard.PlayerSide, refreshIntervalMs: 0), isFirst: false);
            partner.Link.SayName("QaPlayer");
            bot.SayName("BotPartner");
            Assert.That(bot.PartnerName, Is.EqualTo("QaPlayer"));
            Assert.That(partner.Link.PartnerName, Is.EqualTo("BotPartner"));

            int request = partner.Link.Request("join");
            Assert.That(bot.TryReadRequest(request, out string script), Is.True);
            Assert.That(script, Is.EqualTo("join"));
            Assert.That(partner.TryResult(request, out _, out _), Is.False, "no answer yet");

            int mine = partner.Link.Arrive("join");
            Assert.That(partner.Link.Check("join", mine), Is.EqualTo(MeetState.Waiting));
            bot.Arrive("join");
            Assert.That(partner.Link.Check("join", mine), Is.EqualTo(MeetState.Met));

            bot.Answer(request, "done; meet:join done");
            Assert.That(partner.TryResult(request, out string outcome, out string text), Is.True);
            Assert.That(outcome, Is.EqualTo(PairScriptResult.Done));
            Assert.That(text, Is.EqualTo("done; meet:join done"));

            partner.Finish();
            Assert.That(bot.PartnerGone, Is.True, "the bot's serve step ends when the smoke is gone");
            Assert.That(File.Exists(Path.Combine(_dir, "player.log")) && File.Exists(Path.Combine(_dir, "bot.log")), Is.True);
        }

        [Test]
        public void Only_an_answer_that_is_not_done_ends_a_partner_wait_early()
        {
            // PR #254 review 2: party-up's one-entry script answered "done" a frame before the player's own party state, and the
            // wait failed on the answer instead of waiting for the condition.
            var partner = new QaPartner(_dir, refreshIntervalMs: 0);
            var bot = new PairLink(new FilePairBoard(_dir, FilePairBoard.BotSide, FilePairBoard.PlayerSide, refreshIntervalMs: 0), isFirst: false);
            int done = partner.Link.Request("party-up");
            Assert.That(partner.EndsWait(done, out _), Is.False, "no answer yet");
            bot.Answer(done, "done; party:accept done");
            Assert.That(partner.EndsWait(done, out _), Is.False, "a done script lets the condition come");
            int failed = partner.Link.Request("party-up");
            bot.Answer(failed, "failed; party:accept: refused; party:accept failed");
            Assert.That(partner.EndsWait(failed, out string text), Is.True);
            Assert.That(text, Does.StartWith("failed"));
            int cancelled = partner.Link.Request("trade-item");
            bot.Answer(cancelled, PairScriptResult.Cancelled + "; meet:trade-auction: the player gave the request up");
            Assert.That(partner.EndsWait(cancelled, out _), Is.True);
        }

        [Test]
        public void A_cancel_waits_only_for_the_requests_it_gave_up()
        {
            // PR #254 review 6: FreePartner waited for every request since the first, so one given up earlier and never answered
            // cost every later cancel the whole PartnerCancelSeconds.
            var partner = new QaPartner(_dir, refreshIntervalMs: 0);
            var bot = new PairLink(new FilePairBoard(_dir, FilePairBoard.BotSide, FilePairBoard.PlayerSide, refreshIntervalMs: 0), isFirst: false);
            int stuck = partner.Link.Request("loot-bag-stolen");
            Assert.That(partner.Link.CancelUnanswered(), Is.EqualTo(new[] { stuck })); // never answered
            int next = partner.Link.Request("trade-item");
            var cancelled = partner.Link.CancelUnanswered();
            Assert.That(cancelled, Is.EqualTo(new[] { next }));
            Assert.That(partner.AllAnswered(cancelled), Is.False);
            bot.Answer(next, PairScriptResult.Cancelled + "; trade:hold: the player gave the request up");
            Assert.That(partner.AllAnswered(cancelled), Is.True, "the earlier unanswered request is not waited for again");
        }

        [Test]
        public void The_trade_item_price_is_read_from_the_partners_script_row()
        {
            // PR #254 review 7: QaTimeouts.TradeItemGold and the row qa-partner-trade-item held the same price twice.
            Assert.IsTrue(ClientConfig.TryLoad(out var config, out var error), error);
            Assert.That(config.TryGet(typeof(BotBehaviour), out var table), Is.True);
            var rows = table!.Rows.Cast<BotBehaviour>().ToList();
            var row = rows.Single(b => b.Id == QaPartner.TradeItemScript);
            long gold = QaPartner.TradeGold(rows);
            Assert.That(gold, Is.GreaterThan(0));
            Assert.That(row.LoopSteps, Has.Some.EndsWith(":gold=" + gold.ToString(CultureInfo.InvariantCulture)));

            var noGold = new BotBehaviour { Id = QaPartner.TradeItemScript, LoopSteps = new[] { "meet:trade", "trade:m1.demonAsh:nothing" } };
            Assert.Throws<QaFailure>(() => QaPartner.TradeGold(new[] { noGold }));
            Assert.Throws<QaFailure>(() => QaPartner.TradeGold(new BotBehaviour[0]));
        }

        [Test]
        public void Without_the_partner_a_pair_step_is_n_a_partner_needed_else_not_yet()
        {
            var none = Assert.Throws<QaNotApplicable>(() => QaPartner.NotYet(null, "WP-M4-2"));
            Assert.That(none!.Message, Is.EqualTo(QaPartner.NeededReason));
            var notYet = Assert.Throws<QaNotApplicable>(() => QaPartner.NotYet(new QaPartner(_dir), "WP-M4-2"));
            Assert.That(notYet!.Message, Does.StartWith("not yet").And.Contain("WP-M4-2"));
        }
    }
}
