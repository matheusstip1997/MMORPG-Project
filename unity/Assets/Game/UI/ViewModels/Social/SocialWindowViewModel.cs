#nullable enable
using System;
using System.Collections.Generic;
using Shared.Protocol;

namespace Game.UI.ViewModels.Social
{
    /// <summary>The two tabs of the social window (WP-M4-16).</summary>
    public enum SocialTab : byte
    {
        Friends = 0,
        Blocked = 1,
    }

    /// <summary>
    /// The social window (spec 6.5 "M4 social", 9.3 <c>social_window</c>, 9.13; WP-M4-16): the lists as the server last sent them
    /// (<see cref="SocialState"/>: friends with their online state, incoming requests, muted and blocked players), the tab shown
    /// and the name typed. Display only: the server owns the lists and decides every request.
    /// </summary>
    public sealed class SocialWindowViewModel : ViewModel
    {
        private SocialFriendEntry[] _friends = Array.Empty<SocialFriendEntry>();
        private SocialIncomingEntry[] _incoming = Array.Empty<SocialIncomingEntry>();
        private SocialBlockEntry[] _blocks = Array.Empty<SocialBlockEntry>();

        public bool Open { get; private set; }

        public SocialTab Tab { get; private set; }

        /// <summary>The name typed in the field (an online player's display name to add, block or mute).</summary>
        public string Draft { get; private set; } = string.Empty;

        public IReadOnlyList<SocialFriendEntry> Friends => _friends;

        public IReadOnlyList<SocialIncomingEntry> Incoming => _incoming;

        public IReadOnlyList<SocialBlockEntry> Blocks => _blocks;

        /// <summary>Friend requests waiting for an answer (the SO button's badge).</summary>
        public int IncomingCount => _incoming.Length;

        public bool HasState { get; private set; }

        /// <summary>The name field holds a name to send.</summary>
        public bool CanSend => Draft.Trim().Length > 0;

        public void SetOpen(bool open)
        {
            if (Open == open)
                return;
            Open = open;
            NotifyChanged();
        }

        public void Toggle() => SetOpen(!Open);

        public void SetTab(SocialTab tab)
        {
            if (Tab == tab)
                return;
            Tab = tab;
            NotifyChanged();
        }

        public void SetDraft(string text)
        {
            text ??= string.Empty;
            if (text.Length > ChatText.MaxNameChars)
                text = text.Substring(0, ChatText.MaxNameChars);
            if (Draft == text)
                return;
            Draft = text;
            NotifyChanged();
        }

        /// <summary>Takes the typed name to send and empties the field; empty when there is none.</summary>
        public string TakeDraft()
        {
            string name = Draft.Trim();
            if (Draft.Length > 0)
            {
                Draft = string.Empty;
                NotifyChanged();
            }
            return name;
        }

        public void SetState(SocialState state)
        {
            _friends = state.Friends;
            _incoming = state.Incoming;
            _blocks = state.Blocks;
            HasState = true;
            NotifyChanged();
        }

        /// <summary>The connection ended: nothing is known until the next state.</summary>
        public void Clear()
        {
            _friends = Array.Empty<SocialFriendEntry>();
            _incoming = Array.Empty<SocialIncomingEntry>();
            _blocks = Array.Empty<SocialBlockEntry>();
            HasState = false;
            Open = false;
            Draft = string.Empty;
            NotifyChanged();
        }
    }
}
