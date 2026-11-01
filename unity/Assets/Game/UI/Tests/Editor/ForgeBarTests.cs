#nullable enable
using System.Collections.Generic;
using System.Linq;
using Game.Gameplay.Entities;
using Game.Gameplay.Prediction;
using Game.Gameplay.Streaming;
using Game.UI.Config;
using Game.UI.HUD;
using Game.UI.Menus.Forge;
using NUnit.Framework;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Actions;
using Shared.Simulation.Crafting;
using UnityEngine.UIElements;

namespace Game.UI.Tests
{
    /// <summary>
    /// The forge timing bar (spec 5.3 M3 decision 12, 9.13; WP-M3-10): the open round from the session state, the marker on the
    /// client's clock (the shared <see cref="TimingBar"/> math), one press per round, the server's judgement and the scores;
    /// the presenter's track, marker, Strike button and texts; the committed texts.
    /// </summary>
    public sealed class ForgeBarTests
    {
        private static readonly TimingBarParams Bar = new TimingBarParams(1_000, 500, 100, 4_000, 30);

        private sealed class FakeBarRequests : IForgeBarRequests
        {
            public long ForgeBarOffsetMs { get; set; } = -1;
            public List<long> Presses { get; } = new List<long>();

            public uint StrikeForgeBar()
            {
                Presses.Add(ForgeBarOffsetMs);
                return (uint)(100 + Presses.Count);
            }
        }

        private static ConfigSet Tables()
        {
            Assert.That(ClientConfig.TryLoad(out var set, out var error), Is.True, error);
            return set;
        }

        private static ForgeState Running(uint version, uint requestId = 0, byte round = 0, ushort[]? scores = null,
            ForgeHitOutcome outcome = ForgeHitOutcome.None) => new ForgeState
        {
            Version = version, RequestId = requestId, Phase = ForgePhase.Running, Kind = ForgeKind.Mold, DefNetId = 1,
            ChallengeKind = ForgeChallengeKind.TimingBar, ChallengeMax = 150, BarRound = round,
            BarSpeed = round == 0 ? (ushort)0 : (ushort)Bar.SpeedPermillePerSec, BarTarget = round == 0 ? (ushort)0 : (ushort)Bar.TargetPermille,
            BarHalfWidth = round == 0 ? (ushort)0 : (ushort)Bar.HalfWidthPermille, BarDurationMs = round == 0 ? (ushort)0 : (ushort)Bar.DurationMs,
            BarHitMax = round == 0 ? (ushort)0 : (ushort)Bar.HitMaxScore, HitScores = scores ?? new ushort[0], LastOutcome = outcome,
        };

        [Test]
        public void The_marker_follows_the_clients_clock_and_one_press_goes_out_per_round()
        {
            var requests = new FakeBarRequests();
            var model = new ForgeBarModel();
            model.SetRequests(requests);
            model.SetState(Running(1));
            Assert.That((model.Shown, model.Round, model.CanStrike), Is.EqualTo((true, 0, false))); // the bar, no round yet

            model.SetState(Running(2, round: 1));
            requests.ForgeBarOffsetMs = 250;
            Assert.That((model.MarkerPermille, model.PreviewScore, model.CanStrike), Is.EqualTo((250, 0, true)));
            requests.ForgeBarOffsetMs = 500;
            Assert.That((model.MarkerPermille, model.PreviewScore), Is.EqualTo((500, 30))); // on the centre

            Assert.That(model.Strike(), Is.EqualTo(101u));
            Assert.That(model.Strike(), Is.EqualTo(0u)); // one press per round
            Assert.That(requests.Presses, Is.EqualTo(new[] { 500L }));
            Assert.That(model.CanStrike, Is.False);

            model.SetState(Running(3, requestId: 101, scores: new ushort[] { 30 }, outcome: ForgeHitOutcome.Scored));
            Assert.That((model.LastOutcome, model.LastScore, model.Total, model.Round), Is.EqualTo((ForgeHitOutcome.Scored, 30, 30, 0)));

            model.SetState(Running(4, round: 2, scores: new ushort[] { 30 })); // the next hit: a new round, pressable again
            Assert.That((model.Pressed, model.CanStrike, model.LastOutcome), Is.EqualTo((false, true, ForgeHitOutcome.None)));
        }

        [Test]
        public void A_round_that_ran_out_on_the_clients_clock_cannot_be_pressed()
        {
            var requests = new FakeBarRequests();
            var model = new ForgeBarModel();
            model.SetRequests(requests);
            model.SetState(Running(1, round: 1));
            requests.ForgeBarOffsetMs = Bar.DurationMs + 1;
            Assert.That((model.Expired, model.CanStrike), Is.EqualTo((true, false)));
            Assert.That(model.Strike(), Is.EqualTo(0u));
            Assert.That(model.MarkerPermille, Is.EqualTo(TimingBar.Position(Bar, Bar.DurationMs))); // it stops at the round's end
        }

        [Test]
        public void The_bar_shows_its_track_marker_strike_button_and_scores()
        {
            var set = Tables();
            var hud = HudConfig.From(set);
            var requests = new FakeBarRequests();
            var model = new ForgeBarModel();
            model.SetRequests(requests);
            var presenter = new ForgeBarPresenter(hud, new HudView(hud), model);
            presenter.Bind();
            Assert.That(presenter.IsShown, Is.False);

            model.SetState(Running(1, round: 1));
            Assert.That(presenter.IsShown, Is.True);
            Assert.That(presenter.Target.style.left.value, Is.EqualTo(new Length(40, LengthUnit.Percent))); // 500 - 100 permille
            Assert.That(presenter.Target.style.width.value, Is.EqualTo(new Length(20, LengthUnit.Percent)));
            requests.ForgeBarOffsetMs = 750;
            presenter.Animate(0);
            Assert.That(presenter.Marker.style.left.value, Is.EqualTo(new Length(75, LengthUnit.Percent)));
            Assert.That(presenter.StrikeButton.enabledSelf, Is.True);

            model.Strike();
            model.SetState(Running(2, requestId: 101, scores: new ushort[] { 25 }, outcome: ForgeHitOutcome.Scored));
            Assert.That(presenter.OutcomeText, Is.EqualTo("Struck: +25"));
            Assert.That(presenter.HitsText, Is.EqualTo("Hits: 25   Timing 25 / 150"));
        }

        [Test]
        public void The_binder_follows_the_feeds_forge_state_and_clears_on_disconnect()
        {
            var set = Tables();
            var settings = PredictionSettings.From(set);
            Assert.That(ClientWorld.TryLoadGrid("m1", out var grid, out var error), Is.True, error);
            var catalog = ActionCatalog.From(set);
            var requests = new FakeBarRequests();
            var feed = new WorldClientFeed(set, catalog, new EntityRegistry(settings), new LocalPlayerPredictor(settings, grid!, catalog),
                null, null, settings.TickMs) { ForgeBarRequests = requests };
            var model = new ForgeBarModel();
            var binder = new ForgeBarBinder(model);
            binder.Bind(feed);
            feed.RaiseForgeState(Running(1, round: 1));
            requests.ForgeBarOffsetMs = 500;
            Assert.That((model.Round, model.PreviewScore), Is.EqualTo((1, 30)));
            feed.RaiseDisconnected("Kicked");
            Assert.That(model.Shown, Is.False);
            binder.Dispose();
        }

        [Test]
        public void Committed_ui_strings_cover_every_bar_text_id()
        {
            var set = Tables();
            var ui = new HashSet<string>(set.TryGet(typeof(UiStrings), out var table) ? table.Rows.Cast<UiStrings>().Select(r => r.Id) : new string[0]);
            Assert.That(ForgeBarTextIds.All.Where(id => !ui.Contains(id)), Is.Empty);
            foreach (ForgeHitOutcome outcome in System.Enum.GetValues(typeof(ForgeHitOutcome)))
                Assert.That(outcome == ForgeHitOutcome.None || ui.Contains(ForgeBarTextIds.Of(outcome)), Is.True, outcome.ToString());
        }
    }
}
