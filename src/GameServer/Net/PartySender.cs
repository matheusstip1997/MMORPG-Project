using GameServer.Entities;
using GameServer.Social;
using MemoryPack;
using Shared.Protocol;

namespace GameServer.Net;

/// <summary>
/// Phase 10 (spec 6.5, 9.3 <c>party_frame</c>; WP-M4-2): a connected player's party answers and notices
/// (<see cref="PartyResult"/>), the invites it received (<see cref="PartyInvite"/>), then its <see cref="PartyState"/> when its
/// party changed, the client is new, or every <c>PartySettings.stateIntervalMs</c> while it is in a party (HP and positions of
/// members out of interest). Names per recipient through <see cref="DisplayIdentity"/> (rule 8); party members always see
/// each other (R-2.4-03), so nothing hidden is sent (rule 9). Reliable channel; reused writer, messages and member arrays.
/// </summary>
internal sealed class PartySender
{
    private static readonly MemoryPackSerializerOptions Options = MemoryPackSerializerOptions.Utf8;

    private readonly ITransport _transport;
    private readonly IPartyQuery _parties;
    private readonly Func<long, Player?> _playerOf;
    private readonly Func<Player, PartyMemberState, bool> _fill;
    private readonly int _intervalMs;
    private readonly ReliableMessageWriter _writer = new(512);
    private readonly PartyState _state = new();
    private readonly PartyMemberState[][] _members;
    private readonly long[] _ids = new long[ReliableMessages.MaxPartyMembers];

    /// <param name="fill">Writes a member's class, level, online state, HP and position (the world's view); false to leave it out.</param>
    public PartySender(ITransport transport, IPartyQuery parties, Func<long, Player?> playerOf, Func<Player, PartyMemberState, bool> fill,
        int intervalMs)
    {
        _transport = transport;
        _parties = parties;
        _playerOf = playerOf;
        _fill = fill;
        _intervalMs = intervalMs;
        _members = new PartyMemberState[ReliableMessages.MaxPartyMembers + 1][];
        for (int count = 0; count < _members.Length; count++)
        {
            _members[count] = new PartyMemberState[count];
            for (int i = 0; i < count; i++)
                _members[count][i] = new PartyMemberState();
        }
    }

    public long StatesSent { get; private set; }

    public void Send(Player player, long nowMs)
    {
        var answers = player.Party;
        foreach (var result in answers.Results)
            Write(player, ReliableMessageId.PartyResult, result);
        answers.Results.Clear();
        foreach (var invite in answers.Invites)
            Write(player, ReliableMessageId.PartyInvite, invite);
        answers.Invites.Clear();
        if (!answers.Dirty && !(answers.InParty && nowMs >= answers.NextRefreshMs))
            return;
        SendState(player, answers, nowMs);
    }

    private void SendState(Player recipient, PartyAnswers answers, long nowMs)
    {
        long partyId = _parties.PartyIdOf(recipient.CharacterId);
        int count = _parties.MembersOf(partyId, _ids);
        int written = 0;
        var buffer = _members[count];
        for (int i = 0; i < count; i++)
        {
            if (_playerOf(_ids[i]) is not { } member)
                continue;
            var entry = buffer[written];
            entry.EntityId = member.Id;
            entry.Name = DisplayIdentity.For(member, recipient);
            if (entry.Name.Length == 0 || !_fill(member, entry))
                continue;
            written++;
        }
        var members = _members[written];
        if (written != count)
        {
            for (int i = 0; i < written; i++)
                Copy(buffer[i], members[i]);
        }
        _state.Version = ++answers.Version;
        _state.PartyId = written == 0 ? 0 : partyId;
        _state.LeaderEntityId = written == 0 ? 0 : _playerOf(_parties.LeaderOf(partyId))?.Id ?? 0;
        _state.Cause = answers.Cause;
        var cause = answers.CauseCharacterId == 0 ? null : _playerOf(answers.CauseCharacterId);
        _state.CauseEntityId = cause?.Id ?? 0; // never the character id (spec 6.2)
        _state.CauseName = cause != null ? DisplayIdentity.For(cause, recipient) : string.Empty;
        _state.Members = members;
        Write(recipient, ReliableMessageId.PartyState, _state);
        answers.Dirty = false;
        answers.Cause = PartyChangeKind.None;
        answers.CauseCharacterId = 0;
        answers.InParty = written > 0;
        answers.NextRefreshMs = nowMs + _intervalMs;
        StatesSent++;
    }

    private static void Copy(PartyMemberState from, PartyMemberState to)
    {
        to.EntityId = from.EntityId;
        to.Name = from.Name;
        to.ClassNetId = from.ClassNetId;
        to.Level = from.Level;
        to.Online = from.Online;
        to.HpPercent = from.HpPercent;
        to.XCm = from.XCm;
        to.YCm = from.YCm;
    }

    private void Write<T>(Player player, ReliableMessageId id, T message) where T : class
    {
        _writer.Begin(id);
        MemoryPackSerializer.Serialize(_writer, message, Options);
        _transport.Send(player.PeerId, Channel.ReliableOrdered, _writer.Written);
    }
}
