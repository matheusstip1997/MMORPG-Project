#nullable enable
using System.Linq;
using System.Reflection;
using Game.Gameplay.Entities;
using Game.UI.Auth;
using Game.UI.Config;
using Game.UI.Login;
using Game.UI.Presenters;
using NUnit.Framework;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Protocol.Auth;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Game.UI.Tests
{
    /// <summary>Reason code and UI text lookup (spec 9.1), the committed tables, and the layout placement (spec 9.7, 9.10).</summary>
    public sealed class LoginTextAndLayoutTests
    {
        private static ReasonText Reasons(params ReasonCodeStrings[] rows) => new ReasonText(rows);

        [Test]
        public void Reason_text_fills_placeholders()
        {
            var reasons = Reasons(new ReasonCodeStrings { Code = "VersionMismatch", Text = "Server {server}, client {client}.", Icon = "icons/update" });

            Assert.That(reasons.Text("VersionMismatch", new TextArg("server", "3"), new TextArg("client", "1")), Is.EqualTo("Server 3, client 1."));
            Assert.That(reasons.Icon("VersionMismatch"), Is.EqualTo("icons/update"));
        }

        [Test]
        public void A_missing_reason_code_falls_back_to_the_unknown_row_with_the_code()
        {
            var reasons = Reasons(new ReasonCodeStrings { Code = ReasonCodeStrings.UnknownCode, Text = "Something went wrong ({code}).", Icon = "icons/x" });

            Assert.That(reasons.Has("SomethingNew"), Is.False);
            Assert.That(reasons.Text("SomethingNew"), Is.EqualTo("Something went wrong (SomethingNew)."));
            Assert.That(reasons.Icon("SomethingNew"), Is.EqualTo("icons/x"));
        }

        [Test]
        public void Without_the_unknown_row_the_code_itself_is_shown()
        {
            Assert.That(Reasons().Text("NameTaken"), Is.EqualTo("NameTaken"));
            Assert.That(Reasons().Icon("NameTaken"), Is.Empty);
        }

        [Test]
        public void Ui_text_falls_back_to_the_id_and_class_names_to_the_class_id()
        {
            var text = new UiText(new[] { new UiStrings { Id = "class.mage", Text = "Mage" }, new UiStrings { Id = "a", Text = "{x}!" } });

            Assert.That(text.Get("a", new TextArg("x", "hi")), Is.EqualTo("hi!"));
            Assert.That(text.Get("missing"), Is.EqualTo("missing"));
            Assert.That(text.ClassName("mage"), Is.EqualTo("Mage"));
            Assert.That(text.ClassName("ranger"), Is.EqualTo("ranger"));
            Assert.That(TextTemplate.Fill("{a} {b}", new TextArg("a", "1")), Is.EqualTo("1 {b}"), "a missing value stays visible");
        }

        private static ConfigSet Committed()
        {
            Assert.That(ClientConfig.TryLoad(out var set, out var error), Is.True, error);
            return set;
        }

        [Test]
        public void Committed_reason_table_covers_every_reason_code()
        {
            Assert.That(ReasonCodeCoverage.Check(Committed()).Select(e => e.Message), Is.Empty);
        }

        [Test]
        public void Committed_ui_strings_cover_every_text_id()
        {
            var text = LoginConfig.From(Committed()).Text;
            var ids = typeof(UiTextIds).GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.IsLiteral && f.Name != nameof(UiTextIds.ClassPrefix))
                .Select(f => (string)f.GetRawConstantValue());

            Assert.That(ids.Where(id => !text.Has(id)), Is.Empty);
        }

        [Test]
        public void Committed_ui_strings_name_every_initial_class()
        {
            var config = LoginConfig.From(Committed());

            Assert.That(config.Creation.AllowedInitialClasses.Where(c => !config.Text.Has(UiTextIds.ClassPrefix + c)), Is.Empty);
        }

        [Test]
        public void Every_presenter_element_has_a_layout_row()
        {
            var config = LoginConfig.From(Committed());
            using var flow = new LoginFlow(config, new FakeAuthApi(), new FakeTickets(), new FakeConnector(), "test");
            var presenters = new LoginPresenter[]
            {
                new TitlePresenter(config, flow), new ServerSelectPresenter(config, flow),
                new CharacterSelectPresenter(config, flow), new CharacterCreatePresenter(config, flow),
            };

            foreach (var presenter in presenters)
                presenter.Bind();

            // LoginScreenView logs an error for an element without a row.
            LogAssert.NoUnexpectedReceived();
            Assert.That(presenters[2].View.Root.Q<Button>("btn_enter"), Is.Not.Null);
            foreach (var presenter in presenters)
                presenter.Dispose();
        }

        [Test]
        public void Presenters_redraw_from_view_model_events()
        {
            var config = LoginConfig.From(Committed());
            var auth = new FakeAuthApi();
            using var flow = new LoginFlow(config, auth, new FakeTickets(), new FakeConnector(), "1.2.3");
            var title = new TitlePresenter(config, flow);
            title.Bind();
            var prompt = title.View.Root.Q<Label>("press_any_button");
            Assert.That(prompt.text, Is.Empty, "still signing in");

            LoginTestData.Done(flow.SignInAsync());

            Assert.That(prompt.text, Is.EqualTo(config.Text.Get(UiTextIds.PressAnyButton)));
            Assert.That(title.View.Root.Q<Label>("version_label").text, Does.Contain("1.2.3"));
            Assert.That(title.View.Root.Q<Label>("steam_badge").text, Does.Contain("7"));
            title.Dispose();
        }

        [Test]
        public void A_refusal_shows_the_reason_text_from_the_table()
        {
            var config = LoginConfig.From(Committed());
            var auth = new FakeAuthApi();
            auth.Logins.Enqueue(AuthCall<LoginResponse>.Refused(ClientReasonCode.AuthApiUnreachable));
            using var flow = new LoginFlow(config, auth, new FakeTickets(), new FakeConnector(), "1");
            var title = new TitlePresenter(config, flow);
            title.Bind();

            LoginTestData.Done(flow.SignInAsync());

            var message = title.View.Root.Q<Label>("title_message");
            Assert.That(message.text, Is.EqualTo(config.Reasons.Text(nameof(ClientReasonCode.AuthApiUnreachable))));
            Assert.That(message.ClassListContains("login-message--error"), Is.True);
            title.Dispose();
        }

        [Test]
        public void Layout_places_corner_anchors_by_offset()
        {
            var element = new VisualElement();

            UiLayout.Apply(element, new LoginLayout { Id = "s.e", Screen = "s", Anchor = UiAnchor.BottomRight, Offset = new[] { 16, 12 }, Size = new[] { 380, 20 } });

            Assert.That(element.style.position.value, Is.EqualTo(Position.Absolute));
            Assert.That(element.style.right.value.value, Is.EqualTo(16f));
            Assert.That(element.style.bottom.value.value, Is.EqualTo(12f));
            Assert.That((element.style.width.value.value, element.style.height.value.value), Is.EqualTo((380f, 20f)));
            Assert.That(element.style.left.keyword, Is.EqualTo(StyleKeyword.Null));
        }

        [Test]
        public void Layout_centers_on_center_anchors_and_leaves_zero_size_to_content()
        {
            var element = new VisualElement();

            UiLayout.Apply(element, new LoginLayout { Id = "s.e", Screen = "s", Anchor = UiAnchor.Center, Offset = new[] { -30, 40 }, Size = new[] { 0, 0 } });

            Assert.That(element.style.left.value, Is.EqualTo(Length.Percent(50)));
            Assert.That(element.style.top.value, Is.EqualTo(Length.Percent(50)));
            Assert.That(element.style.marginLeft.value.value, Is.EqualTo(-30f));
            Assert.That(element.style.marginTop.value.value, Is.EqualTo(40f));
            Assert.That(element.style.translate.value.x, Is.EqualTo(Length.Percent(-50)));
            Assert.That(element.style.width.keyword, Is.EqualTo(StyleKeyword.Null));
        }

        [Test]
        public void Committed_login_settings_use_the_reference_resolution_of_the_spec()
        {
            var settings = Committed().Single<LoginSettings>();

            Assert.That((settings.ReferenceWidth, settings.ReferenceHeight, settings.Scaling), Is.EqualTo((1280, 720, UiScaleMode.FitHeight)));
        }
    }
}
