#nullable enable
using System;
using Game.Gameplay.Entities;
using Game.UI.Config;
using Game.UI.ViewModels.Hud;
using Shared.Protocol;

namespace Game.UI.HUD
{
    /// <summary>
    /// The party on the HUD (spec 6.5, 9.3 <c>party_frame</c>; WP-M4-2): the server's <see cref="PartyState"/> fills the frame
    /// (the own player left off) and each change shows its toast ("A joined", "A left", "B leads"; the name as the server
    /// sent it); an invite opens the invite toast with its countdown; a refused request or a notice about an own invite
    /// shows its <c>ReasonCodeStrings</c> text, an accepted invite request "Party invite sent". The frame's and the toast's
    /// buttons send through <see cref="WorldClientFeed.PartyRequests"/>; the server decides everything.
    /// </summary>
    public sealed class PartyBinder : IDisposable
    {
        private readonly PartyFrameViewModel _frame;
        private readonly PartyInviteViewModel _invite;
        private readonly ToastViewModel _toast;
        private readonly int _toastMs;
        private readonly Func<double> _now;
        private WorldClientFeed? _feed;

        public PartyBinder(PartyFrameViewModel frame, PartyInviteViewModel invite, ToastViewModel toast, int toastMs, Func<double> nowMs)
        {
            _frame = frame;
            _invite = invite;
            _toast = toast;
            _toastMs = toastMs;
            _now = nowMs;
        }

        public WorldClientFeed? Feed => _feed;

        public void Bind(WorldClientFeed feed)
        {
            Unbind();
            _feed = feed;
            feed.PartyReceived += OnState;
            feed.PartyInvited += OnInvite;
            feed.PartyAnswered += OnAnswer;
            feed.Disconnected += OnDisconnected;
            if (feed.Party?.State is { } state)
                _frame.Set(state, feed.Registry.OwnEntityId ?? 0);
        }

        public void Unbind()
        {
            if (_feed == null)
                return;
            _feed.PartyReceived -= OnState;
            _feed.PartyInvited -= OnInvite;
            _feed.PartyAnswered -= OnAnswer;
            _feed.Disconnected -= OnDisconnected;
            _feed = null;
        }

        public void Dispose() => Unbind();

        /// <summary>Every frame: the invite toast's countdown.</summary>
        public void Frame(double nowMs) => _invite.Tick(nowMs);

        public void Accept() => Answer(PartyAction.Accept);

        public void Decline() => Answer(PartyAction.Decline);

        public void Leave() => _feed?.PartyRequests?.RequestParty(PartyAction.Leave);

        /// <summary>The leader removes the member with this entity.</summary>
        public void Kick(uint entityId) => _feed?.PartyRequests?.RequestParty(PartyAction.Kick, targetEntityId: entityId);

        /// <summary>The leader makes the member with this entity the leader.</summary>
        public void PassLead(uint entityId) => _feed?.PartyRequests?.RequestParty(PartyAction.PassLead, targetEntityId: entityId);

        private void Answer(PartyAction action)
        {
            if (!_invite.Visible)
                return;
            uint inviteId = _invite.InviteId;
            _invite.Hide();
            _feed?.PartyRequests?.RequestParty(action, inviteId);
        }

        private void OnState(PartyState state)
        {
            _frame.Set(state, _feed?.Registry.OwnEntityId ?? 0);
            string textId = HudTextIds.PartyChangeOf(state.Cause);
            if (textId.Length > 0)
                _toast.ShowText(textId, "name", state.CauseName, _now(), _toastMs);
        }

        private void OnInvite(PartyInvite invite) => _invite.Show(invite, _now());

        private void OnAnswer(PartyResult result)
        {
            if (result.Reason != PartyReason.None)
                _toast.Show(result.Reason.ToString(), false, _now(), _toastMs);
            else if (result.Action == PartyAction.Invite && result.RequestId != 0)
                _toast.ShowText(HudTextIds.PartyInviteSent, "", "", _now(), _toastMs);
        }

        private void OnDisconnected(string reasonCode)
        {
            _frame.Clear();
            _invite.Hide();
        }
    }
}
