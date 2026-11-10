#nullable enable
using System;
using Shared.Protocol;

namespace Game.UI.ViewModels.Trade
{
    /// <summary>
    /// The trade window's state (spec 4.5 "M4 trade window", 9.13; WP-M4-4): open while the server says the window is open or
    /// the swap is saving; both offers and golds, both locks and confirms as the newest <see cref="TradeState"/> describes them;
    /// the confirm countdown once both are locked; the request waiting for its answer. It only describes; the server checks
    /// every request, re-validates everything at the confirm and swaps in one transaction.
    /// </summary>
    public sealed class TradeWindowViewModel : ViewModel
    {
        private TradeState? _state;
        private double _deadlineMs;
        private int _secondsLeft;

        public bool Open => _state is { Phase: TradePhase.Open or TradePhase.Settling };

        /// <summary>Both confirmed: the swap is applied and saving; nothing can change any more.</summary>
        public bool Saving => _state is { Phase: TradePhase.Settling };

        public uint TradeId => _state?.TradeId ?? 0;

        public string PartnerName => _state?.PartnerName ?? string.Empty;

        public TradeOffer[] OwnOffer => _state?.OwnOffer ?? Array.Empty<TradeOffer>();

        public TradeOffer[] PartnerOffer => _state?.PartnerOffer ?? Array.Empty<TradeOffer>();

        public long OwnGold => _state?.OwnGold ?? 0;

        public long PartnerGold => _state?.PartnerGold ?? 0;

        public bool OwnLocked => _state?.OwnLocked ?? false;

        public bool PartnerLocked => _state?.PartnerLocked ?? false;

        public bool OwnConfirmed => _state?.OwnConfirmed ?? false;

        public bool PartnerConfirmed => _state?.PartnerConfirmed ?? false;

        public bool BothLocked => OwnLocked && PartnerLocked;

        /// <summary>Whole seconds left to confirm once both are locked (the server's timeout), else 0.</summary>
        public int SecondsLeft => BothLocked && Open && !Saving ? _secondsLeft : 0;

        public uint Pending { get; private set; }

        public bool CanEdit => Open && !Saving && Pending == 0;

        /// <summary>The gold field's text as typed; null when it shows the offer (nothing typed since).</summary>
        public string? GoldDraft { get; private set; }

        /// <summary>
        /// The gold field holds an amount that is not the offer yet (typed, not set): Lock and Confirm wait for Set gold or Enter,
        /// so a lock never holds an amount other than the one the player sees.
        /// </summary>
        public bool GoldDirty => GoldDraft != null && GoldDraft.Trim() != OwnGold.ToString(System.Globalization.CultureInfo.InvariantCulture);

        public bool CanLock => CanEdit && !OwnLocked && !GoldDirty;

        public bool CanConfirm => Open && !Saving && BothLocked && !OwnConfirmed && Pending == 0 && !GoldDirty;

        /// <summary>The player typed in the gold field.</summary>
        public void EditGold(string text)
        {
            if (GoldDraft == text)
                return;
            GoldDraft = text;
            NotifyChanged();
        }

        public bool CanCancel => Open && !Saving;

        /// <summary>A new state from the server; <paramref name="nowMs"/> starts its countdown.</summary>
        public void Apply(TradeState state, double nowMs)
        {
            bool sameTrade = _state != null && _state.TradeId == state.TradeId && state.Phase is TradePhase.Open or TradePhase.Settling;
            _state = state;
            if (!sameTrade || (GoldDraft != null && !GoldDirty))
                GoldDraft = null; // a new trade, the window closed, or the offer now holds what was typed: the field shows the offer
            _deadlineMs = nowMs + state.MsLeft;
            _secondsLeft = Seconds(state.MsLeft);
            if (!Open)
                Pending = 0;
            NotifyChanged();
        }

        /// <summary>Once per frame: notifies only when the whole-second countdown changes.</summary>
        public void Tick(double nowMs)
        {
            if (!Open || !BothLocked)
                return;
            int seconds = Seconds(_deadlineMs - nowMs);
            if (seconds == _secondsLeft)
                return;
            _secondsLeft = seconds;
            NotifyChanged();
        }

        public void Sent(uint requestId)
        {
            Pending = requestId;
            NotifyChanged();
        }

        /// <summary>An answer: the refusal's reason code to show, or null (accepted, or not this window's request).</summary>
        public string? OnResult(TradeResult result)
        {
            if (result.RequestId != Pending || Pending == 0)
                return null;
            Pending = 0;
            NotifyChanged();
            return result.Reason == TradeReason.None ? null : result.Reason.ToString();
        }

        public void Clear()
        {
            _state = null;
            GoldDraft = null;
            Pending = 0;
            _secondsLeft = 0;
            NotifyChanged();
        }

        private static int Seconds(double ms) => ms <= 0 ? 0 : (int)Math.Ceiling(ms / 1000.0);
    }

    /// <summary>
    /// The trade invite prompt (WP-M4-4): for an invite to this player (<see cref="TradePhase.Invited"/>: Accept, Decline and the
    /// time left) and while this player's own invite waits (<see cref="TradePhase.Inviting"/>: Cancel). Hidden otherwise.
    /// </summary>
    public sealed class TradeInviteViewModel : ViewModel
    {
        private double _deadlineMs;

        public bool Visible => Phase is TradePhase.Invited or TradePhase.Inviting;

        public TradePhase Phase { get; private set; }

        /// <summary>This player was invited (true) or invited the partner (false).</summary>
        public bool Incoming => Phase == TradePhase.Invited;

        public string PartnerName { get; private set; } = string.Empty;

        public int SecondsLeft { get; private set; }

        public void Apply(TradeState state, double nowMs)
        {
            Phase = state.Phase;
            PartnerName = state.PartnerName;
            _deadlineMs = nowMs + state.MsLeft;
            SecondsLeft = Seconds(state.MsLeft);
            NotifyChanged();
        }

        public void Tick(double nowMs)
        {
            if (!Visible)
                return;
            int seconds = Seconds(_deadlineMs - nowMs);
            if (seconds == SecondsLeft)
                return;
            SecondsLeft = seconds;
            NotifyChanged();
        }

        /// <summary>Hides the prompt at once after the player answered (the server's next state confirms it).</summary>
        public void Hide()
        {
            if (!Visible)
                return;
            Phase = TradePhase.None;
            NotifyChanged();
        }

        public void Clear()
        {
            Phase = TradePhase.None;
            PartnerName = string.Empty;
            SecondsLeft = 0;
            NotifyChanged();
        }

        private static int Seconds(double ms) => ms <= 0 ? 0 : (int)Math.Ceiling(ms / 1000.0);
    }
}
