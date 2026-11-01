#nullable enable
using System;
using System.Collections.Generic;
using Game.Gameplay.Entities;
using Game.UI.ViewModels;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Crafting;

namespace Game.UI.Menus.Forge
{
    /// <summary>
    /// <c>UiStrings</c> ids of the forge timing bar (spec 9.1: no user-facing text in code; spec 5.3 M3 decision 12, 9.13;
    /// WP-M3-10). An EditMode test checks that each constant has a row.
    /// </summary>
    public static class ForgeBarTextIds
    {
        public const string Title = "forge.bar.title";
        public const string Strike = "forge.bar.strike";
        /// <summary>Hits: {scores}.</summary>
        public const string Hits = "forge.bar.hits";
        /// <summary>Timing {score} / {max}.</summary>
        public const string Total = "forge.bar.total";
        /// <summary>Struck: +{score}.</summary>
        public const string Scored = "forge.bar.outcome.scored";
        public const string Missed = "forge.bar.outcome.missed";
        public const string Late = "forge.bar.outcome.late";
        public const string Impossible = "forge.bar.outcome.impossible";
        public const string Repeat = "forge.bar.outcome.repeat";
        public const string Dropped = "forge.bar.outcome.dropped";

        public static readonly string[] All = { Title, Strike, Hits, Total, Scored, Missed, Late, Impossible, Repeat, Dropped };

        /// <summary>The text of an outcome; empty for none.</summary>
        public static string Of(ForgeHitOutcome outcome) => outcome switch
        {
            ForgeHitOutcome.Scored => Scored,
            ForgeHitOutcome.Missed => Missed,
            ForgeHitOutcome.Late => Late,
            ForgeHitOutcome.Impossible => Impossible,
            ForgeHitOutcome.Repeat => Repeat,
            ForgeHitOutcome.Dropped => Dropped,
            _ => string.Empty,
        };
    }

    /// <summary>
    /// The forge timing bar's state (spec 5.3 M3 decision 12, 9.13; WP-M3-10), engine-free: the open round the server sent in
    /// the session's <see cref="ForgeState"/>, the marker where the client's clock puts it (<see cref="TimingBar.Position"/>,
    /// the same math the server scores with), what a press now would score, the scores so far and how the last press was
    /// judged. One press per round goes out through <see cref="IForgeBarRequests"/>; the server judges it on its own timeline
    /// and decides the score.
    /// </summary>
    public sealed class ForgeBarModel : ViewModel
    {
        private static readonly ushort[] NoScores = new ushort[0];
        private IForgeBarRequests? _requests;
        private int _round;

        /// <summary>The running session as last sent (null without one).</summary>
        public ForgeState? Session { get; private set; }

        /// <summary>The session's job plays the timing bar.</summary>
        public bool Shown => Session != null && Session.ChallengeKind == ForgeChallengeKind.TimingBar;

        /// <summary>The open round (from 1), 0 when none is open.</summary>
        public int Round => Session?.BarRound ?? 0;

        public TimingBarParams Bar => Round == 0 ? default
            : new TimingBarParams(Session!.BarSpeed, Session.BarTarget, Session.BarHalfWidth, Session.BarDurationMs, Session.BarHitMax);

        /// <summary>The ms since the open round reached this client; -1 without one.</summary>
        public long OffsetMs => Round == 0 || _requests == null ? -1 : _requests.ForgeBarOffsetMs;

        /// <summary>The open round ran out on this client's clock (a press now would be late).</summary>
        public bool Expired => Round != 0 && OffsetMs > Bar.DurationMs;

        /// <summary>The marker's position now, permille of the bar (it stops at the round's end).</summary>
        public int MarkerPermille
        {
            get
            {
                long offset = OffsetMs;
                if (offset < 0)
                    return 0;
                var bar = Bar;
                return TimingBar.Position(in bar, Math.Min(offset, bar.DurationMs));
            }
        }

        /// <summary>What a press now would score (a preview; the server decides).</summary>
        public int PreviewScore
        {
            get
            {
                var bar = Bar;
                long offset = OffsetMs;
                return offset < 0 ? 0 : TimingBar.Score(in bar, offset);
            }
        }

        /// <summary>A press went out for the open round.</summary>
        public bool Pressed { get; private set; }

        /// <summary>The press waiting for its answer, 0 for none.</summary>
        public uint PendingStrike { get; private set; }

        public bool CanStrike => Round != 0 && !Pressed && !Expired && OffsetMs >= 0;

        /// <summary>The closed rounds' scores, in order.</summary>
        public IReadOnlyList<ushort> Scores => Session?.HitScores ?? NoScores;

        /// <summary>The closed rounds' sum, capped by the session's maximum.</summary>
        public int Total
        {
            get
            {
                int sum = 0;
                foreach (var score in Scores)
                    sum += score;
                int max = Session?.ChallengeMax ?? 0;
                return sum < max ? sum : max;
            }
        }

        /// <summary>How the server judged the last press (none before one).</summary>
        public ForgeHitOutcome LastOutcome { get; private set; }

        /// <summary>The score of the last judged press.</summary>
        public int LastScore { get; private set; }

        public void SetRequests(IForgeBarRequests? requests)
        {
            _requests = requests;
            NotifyChanged();
        }

        /// <summary>A session state came: a new round is unpressed; the answer to the press carries its outcome.</summary>
        public void SetState(ForgeState state)
        {
            Session = state.Phase == ForgePhase.Running || state.Phase == ForgePhase.Finishing ? state : null;
            if (Round != 0 && Round != _round)
            {
                Pressed = false;
                LastOutcome = ForgeHitOutcome.None;
            }
            _round = Round;
            if (PendingStrike != 0 && state.RequestId == PendingStrike)
            {
                PendingStrike = 0;
                LastOutcome = state.LastOutcome;
                LastScore = state.HitScores.Length > 0 ? state.HitScores[state.HitScores.Length - 1] : 0;
            }
            NotifyChanged();
        }

        /// <summary>The press on the open round; 0 when it cannot go out.</summary>
        public uint Strike()
        {
            if (!CanStrike || _requests == null)
                return 0;
            uint id = _requests.StrikeForgeBar();
            if (id == 0)
                return 0;
            Pressed = true;
            PendingStrike = id;
            NotifyChanged();
            return id;
        }

        public void Clear()
        {
            Session = null;
            _round = 0;
            Pressed = false;
            PendingStrike = 0;
            LastOutcome = ForgeHitOutcome.None;
            LastScore = 0;
            NotifyChanged();
        }
    }

    /// <summary>Connects the forge timing bar (WP-M3-10) to the world client: the session states and the press.</summary>
    public sealed class ForgeBarBinder : IDisposable
    {
        private readonly ForgeBarModel _model;
        private WorldClientFeed? _feed;

        public ForgeBarBinder(ForgeBarModel model)
        {
            _model = model;
        }

        public void Bind(WorldClientFeed feed)
        {
            Unbind();
            _feed = feed;
            feed.ForgeStateReceived += _model.SetState;
            feed.Disconnected += OnDisconnected;
            _model.SetRequests(feed.ForgeBarRequests);
            if (feed.ForgeState != null)
                _model.SetState(feed.ForgeState);
        }

        public void Unbind()
        {
            if (_feed == null)
                return;
            _feed.ForgeStateReceived -= _model.SetState;
            _feed.Disconnected -= OnDisconnected;
            _feed = null;
        }

        public void Dispose() => Unbind();

        private void OnDisconnected(string reasonCode) => _model.Clear();
    }
}
