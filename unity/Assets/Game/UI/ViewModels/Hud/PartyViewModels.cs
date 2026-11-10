#nullable enable
using System;
using System.Collections.Generic;
using Shared.Protocol;

namespace Game.UI.ViewModels.Hud
{
    /// <summary>One other member on the party frame, keyed by its entity (no character id reaches a client, spec 6.2).</summary>
    public readonly struct PartyFrameRow
    {
        public PartyFrameRow(uint entityId, string name, int level, bool online, int hpPercent, bool isLeader)
        {
            EntityId = entityId;
            Name = name;
            Level = level;
            Online = online;
            HpPercent = hpPercent;
            IsLeader = isLeader;
        }

        public uint EntityId { get; }

        /// <summary>The name the server sent for this player (<c>DisplayIdentity</c>).</summary>
        public string Name { get; }

        public int Level { get; }

        /// <summary>False while the member lingers after a disconnect.</summary>
        public bool Online { get; }

        /// <summary>HP in whole percent, 0 when dead.</summary>
        public int HpPercent { get; }

        public bool IsLeader { get; }
    }

    /// <summary>
    /// Spec 9.3 <c>party_frame</c> (WP-M4-2): the other members of the own party in join order, with name, level, the leader
    /// mark, online and HP percent from the server's <see cref="PartyState"/> (refreshed at <c>PartySettings.stateIntervalMs</c>
    /// and on every change), and whether the own player leads (the kick and lead buttons show then). Hidden outside a party.
    /// </summary>
    public sealed class PartyFrameViewModel : ViewModel
    {
        private readonly List<PartyFrameRow> _rows = new List<PartyFrameRow>();

        public IReadOnlyList<PartyFrameRow> Rows => _rows;

        public bool InParty { get; private set; }

        /// <summary>The own player leads the party: it may kick and pass the lead.</summary>
        public bool OwnIsLeader { get; private set; }

        /// <summary>The state as the server sent it; <paramref name="ownEntityId"/> is left off the rows.</summary>
        public void Set(PartyState state, uint ownEntityId)
        {
            _rows.Clear();
            InParty = state.PartyId != 0 && state.Members.Length > 0;
            OwnIsLeader = InParty && state.LeaderEntityId == ownEntityId;
            if (InParty)
            {
                foreach (var member in state.Members)
                {
                    if (member.EntityId == ownEntityId)
                        continue;
                    _rows.Add(new PartyFrameRow(member.EntityId, member.Name, member.Level, member.Online, member.HpPercent,
                        member.EntityId == state.LeaderEntityId));
                }
            }
            NotifyChanged();
        }

        public void Clear()
        {
            if (!InParty && _rows.Count == 0)
                return;
            _rows.Clear();
            InParty = false;
            OwnIsLeader = false;
            NotifyChanged();
        }
    }

    /// <summary>
    /// The party invite toast (<c>party_invite</c>, WP-M4-2): the inviter's name as the server sent it and the seconds left
    /// before the invite lapses; it hides when answered or when the time is up.
    /// </summary>
    public sealed class PartyInviteViewModel : ViewModel
    {
        public bool Visible { get; private set; }

        public uint InviteId { get; private set; }

        public string InviterName { get; private set; } = "";

        public double ExpiresAtMs { get; private set; }

        /// <summary>Whole seconds left, counted up (a toast never shows 0 while it is up).</summary>
        public int SecondsLeft { get; private set; }

        public void Show(PartyInvite invite, double nowMs)
        {
            Visible = true;
            InviteId = invite.InviteId;
            InviterName = invite.InviterName;
            ExpiresAtMs = nowMs + invite.TimeoutMs;
            SecondsLeft = Seconds(nowMs);
            NotifyChanged();
        }

        /// <summary>Every frame: the countdown, and the toast hides once the invite lapsed.</summary>
        public void Tick(double nowMs)
        {
            if (!Visible)
                return;
            if (nowMs >= ExpiresAtMs)
            {
                Hide();
                return;
            }
            int seconds = Seconds(nowMs);
            if (seconds == SecondsLeft)
                return;
            SecondsLeft = seconds;
            NotifyChanged();
        }

        public void Hide()
        {
            if (!Visible)
                return;
            Visible = false;
            InviteId = 0;
            NotifyChanged();
        }

        private int Seconds(double nowMs) => Math.Max(1, (int)Math.Ceiling((ExpiresAtMs - nowMs) / 1000.0));
    }
}
