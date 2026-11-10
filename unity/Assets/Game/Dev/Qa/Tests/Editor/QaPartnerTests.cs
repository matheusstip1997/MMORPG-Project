#nullable enable
using System.IO;
using Game.Gameplay.Entities;
using NUnit.Framework;

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
        public void Without_the_partner_a_pair_step_is_n_a_partner_needed_else_not_yet()
        {
            var none = Assert.Throws<QaNotApplicable>(() => QaPartner.NotYet(null, "WP-M4-2"));
            Assert.That(none!.Message, Is.EqualTo(QaPartner.NeededReason));
            var notYet = Assert.Throws<QaNotApplicable>(() => QaPartner.NotYet(new QaPartner(_dir), "WP-M4-2"));
            Assert.That(notYet!.Message, Does.StartWith("not yet").And.Contain("WP-M4-2"));
        }
    }
}
