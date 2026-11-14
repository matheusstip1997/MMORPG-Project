#nullable enable
using System;
using System.Collections.Generic;

namespace Game.UI.HUD
{
    /// <summary>
    /// Windows that open one at a time. User decision 2026-11-14 ("Yes, one at a time", spec 9.13): the mail, auction, stall and
    /// loot bag windows at the left edge of the screen. Opening any of them closes the others, a trade closes them all, and none of
    /// them opens while a trade is open (the binders ask <see cref="IsBlocked"/> before they open). Each window joins with its open
    /// state and its own close path, so a close made here does what the window's own close does (a stall's close leaves the stall,
    /// a loot bag's tells the server, a focused field gives the gameplay keys back, an auction's pending answer still toasts).
    /// <see cref="Frame"/> runs once per frame after the windows' binders, so a window opened this frame (by its key, its prompt
    /// or a server's answer) closes the others before anything is drawn. Engine-free; allocates nothing per frame.
    /// </summary>
    public sealed class WindowGroup
    {
        private readonly List<Member> _members = new List<Member>();

        /// <summary>True while the windows may not be open (a trade is open); null: never.</summary>
        public Func<bool>? Blocked { get; set; }

        public bool IsBlocked => Blocked?.Invoke() ?? false;

        public int Count => _members.Count;

        /// <summary>A window joins the group: <paramref name="isOpen"/> reads its state, <paramref name="close"/> is its own close.</summary>
        public void Join(string id, Func<bool> isOpen, Action close)
        {
            _members.Add(new Member(id, isOpen, close) { WasOpen = isOpen() });
        }

        /// <summary>
        /// Once per frame: while blocked every open window closes; otherwise a window that opened since the last frame closes the
        /// others (two opened in the same frame: the one that joined last stays).
        /// </summary>
        public void Frame()
        {
            bool blocked = IsBlocked;
            int opened = -1;
            for (int i = 0; i < _members.Count; i++)
            {
                if (_members[i].IsOpen() && !_members[i].WasOpen)
                    opened = i;
            }
            for (int i = 0; i < _members.Count; i++)
            {
                var member = _members[i];
                if (member.IsOpen() && (blocked || (opened >= 0 && i != opened)))
                    member.Close();
                member.WasOpen = member.IsOpen();
            }
        }

        /// <summary>The id of the window open now, or null (for tests and the QA checks).</summary>
        public string? OpenId()
        {
            for (int i = 0; i < _members.Count; i++)
            {
                if (_members[i].IsOpen())
                    return _members[i].Id;
            }
            return null;
        }

        private sealed class Member
        {
            public Member(string id, Func<bool> isOpen, Action close)
            {
                Id = id;
                IsOpen = isOpen;
                Close = close;
            }

            public string Id { get; }
            public Func<bool> IsOpen { get; }
            public Action Close { get; }
            public bool WasOpen { get; set; }
        }
    }
}
