#nullable enable
using System.Collections.Generic;
using System.Linq;
using Game.Gameplay.Entities;
using Game.Gameplay.Prediction;
using Game.Gameplay.Streaming;
using Game.UI.Config;
using Game.UI.HUD;
using Game.UI.ViewModels.Hud;
using NUnit.Framework;
using Shared.Protocol;
using Shared.Simulation.Actions;
using UnityEngine.UIElements;

namespace Game.UI.Tests
{
    /// <summary>
    /// The local chat panel (spec 9.12, R-6.5-01): the view model (history, input state, the shared text check), the
    /// presenter (Enter opens and sends, Escape closes, lines as plain text from the tables) and the feed binding (lines
    /// and refusals in, the line out through the feed, gameplay keys suspended while typing).
    /// </summary>
    public sealed class HudChatTests
    {
        private static HudConfig Committed()
        {
            Assert.That(ClientConfig.TryLoad(out var set, out var error), Is.True, error);
            return HudConfig.From(set);
        }

        [Test]
        public void The_history_keeps_the_newest_lines()
        {
            var model = new ChatViewModel(historyLines: 3, maxChars: 20);

            for (int i = 0; i < 5; i++)
                model.AddLine(1, "Aria", "line " + i);

            Assert.That(model.Lines.Select(l => l.Text), Is.EqualTo(new[] { "line 2", "line 3", "line 4" }));
        }

        [Test]
        public void Opening_and_closing_the_input_change_once()
        {
            var model = new ChatViewModel(3, 20);
            int changes = 0;
            model.Changed += () => changes++;

            model.OpenInput();
            model.OpenInput();
            model.CloseInput();
            model.CloseInput();

            Assert.That(changes, Is.EqualTo(2));
        }

        [Test]
        public void Submit_closes_the_input_and_returns_the_trimmed_line()
        {
            var model = new ChatViewModel(3, 20);
            model.OpenInput();

            Assert.That(model.Submit("  hello  "), Is.EqualTo("hello"));
            Assert.That(model.InputOpen, Is.False);
            Assert.That(model.Lines, Is.Empty); // the line shows when the server's echo arrives
        }

        [Test]
        public void Submit_refuses_a_line_over_max_chars_at_once_and_an_empty_one_silently()
        {
            var model = new ChatViewModel(3, 5);

            Assert.That(model.Submit("123456"), Is.Null);
            Assert.That(model.Submit("   "), Is.Null);

            var line = model.Lines.Single();
            Assert.That((line.Kind, line.Text), Is.EqualTo((ChatLineKind.Refusal, nameof(ChatReason.ChatTooLong))));
        }

        [Test]
        public void Enter_opens_the_input_and_suspends_typing_until_it_sends()
        {
            var config = Committed();
            var model = new ChatViewModel(config.Chat.HistoryLines, config.Chat.MaxChars);
            var sent = new List<string>();
            var presenter = Bind(config, model, sent.Add);
            Assert.That(presenter.InputVisible, Is.False);

            presenter.PressEnter();
            Assert.That((model.InputOpen, presenter.InputVisible), Is.EqualTo((true, true)));

            presenter.Draft = "hello there";
            presenter.PressEnter();

            Assert.That(sent, Is.EqualTo(new[] { "hello there" }));
            Assert.That((model.InputOpen, presenter.InputVisible, presenter.Draft), Is.EqualTo((false, false, "")));
        }

        [Test]
        public void Escape_closes_the_input_and_keeps_the_draft()
        {
            var config = Committed();
            var model = new ChatViewModel(config.Chat.HistoryLines, config.Chat.MaxChars);
            var sent = new List<string>();
            var presenter = Bind(config, model, sent.Add);
            presenter.PressEnter();
            presenter.Draft = "half a tho";

            presenter.PressEscape();

            Assert.That((model.InputOpen, presenter.Draft), Is.EqualTo((false, "half a tho")));
            Assert.That(sent, Is.Empty);
        }

        [Test]
        public void Lines_show_the_name_template_and_refusals_their_reason_text_as_plain_text()
        {
            var config = Committed();
            var model = new ChatViewModel(config.Chat.HistoryLines, config.Chat.MaxChars);
            var presenter = Bind(config, model, _ => { });

            model.AddLine(7, "Aria", "<color=red>hi</color>");
            model.AddRefusal(nameof(ChatReason.ChatRateLimited));

            var name = config.Text.Get(HudTextIds.ChatSender, new TextArg("name", "Aria"));
            Assert.That(presenter.ShownLines(), Is.EqualTo(new[]
            {
                name + " <color=red>hi</color>",
                config.Reasons.Text(nameof(ChatReason.ChatRateLimited)),
            }));
            Assert.That(config.Reasons.Has(nameof(ChatReason.ChatRateLimited)), Is.True);
        }

        [Test]
        public void Lines_and_refusals_from_the_feed_reach_the_panel_and_the_typing_flag_reaches_the_feed()
        {
            var config = Committed();
            var models = new HudViewModels(4, 4, config.Chat.HistoryLines, config.Chat.MaxChars);
            var feed = Feed();
            var binder = new HudFeedBinder(config, models, () => 0);
            binder.Bind(feed);

            feed.RaiseChatLine(3, "Aria", "well met");
            feed.RaiseChatRefused(ChatReason.ChatRateLimited);
            models.Chat.OpenInput();

            Assert.That(models.Chat.Lines.Select(l => (l.Kind, l.Name, l.Text)), Is.EqualTo(new[]
            {
                (ChatLineKind.Player, "Aria", "well met"),
                (ChatLineKind.Refusal, "", nameof(ChatReason.ChatRateLimited)),
            }));
            Assert.That(feed.TextInputActive, Is.True);

            models.Chat.CloseInput();
            Assert.That(feed.TextInputActive, Is.False);
            binder.Dispose();
        }

        [Test]
        public void A_submitted_line_goes_out_through_the_feed_or_shows_that_it_was_not_sent()
        {
            var config = Committed();
            var models = new HudViewModels(4, 4, config.Chat.HistoryLines, config.Chat.MaxChars);
            var feed = Feed();
            var sent = new List<string>();
            bool connected = true;
            feed.ChatSender = text =>
            {
                sent.Add(text);
                return connected;
            };
            var binder = new HudFeedBinder(config, models, () => 0);
            binder.Bind(feed);

            binder.SubmitChat(" first ");
            connected = false;
            binder.SubmitChat("second");

            Assert.That(sent, Is.EqualTo(new[] { "first", "second" }));
            Assert.That(models.Chat.Lines.Single().Text, Is.EqualTo(nameof(ClientReasonCode.ConnectionLost)));
            binder.Dispose();
        }

        [Test]
        public void Every_chat_reason_has_reason_text()
        {
            var config = Committed();

            foreach (var reason in System.Enum.GetNames(typeof(ChatReason)).Where(n => n != nameof(ChatReason.None)))
                Assert.That(config.Reasons.Has(reason), Is.True, reason);
        }

        private static ChatPresenter Bind(HudConfig config, ChatViewModel model, System.Action<string> submit)
        {
            var view = new HudView(config);
            var presenter = new ChatPresenter(config, view, model, submit);
            presenter.Bind();
            // Focus needs a panel; the presenter works without one (the field just is not focused).
            Assert.That(view.Root.Q<TextField>("chat_input"), Is.Not.Null);
            return presenter;
        }

        private static WorldClientFeed Feed()
        {
            Assert.That(ClientConfig.TryLoad(out var set, out var error), Is.True, error);
            Assert.That(ClientWorld.TryLoadGrid("m1", out var grid, out error), Is.True, error);
            var settings = PredictionSettings.From(set);
            var catalog = ActionCatalog.From(set);
            return new WorldClientFeed(set, catalog, new EntityRegistry(settings), new LocalPlayerPredictor(settings, grid!, catalog),
                null, null, settings.TickMs);
        }
    }
}
