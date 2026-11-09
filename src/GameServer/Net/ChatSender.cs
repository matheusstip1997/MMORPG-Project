using GameServer.Entities;
using GameServer.Social;
using GameServer.World;
using Shared.Protocol;

namespace GameServer.Net;

/// <summary>
/// Delivers chat in tick phase 10, every tick (spec 1.7: reliable channel; R-6.5-01; channels WP-M4-2). Each accepted line
/// goes, as a <see cref="ChatLineMessage"/>, to its channel's recipients, in id order, and to the sender itself (the echo that
/// shows its line was accepted):
/// <list type="bullet">
/// <item>Local: every connected player whose interest holds the sender. Interest already applies <c>IVisibilityQuery</c>, so
/// a player who cannot see the sender never gets its line (rule 9).</item>
/// <item>Party: every connected member of the sender's party, wherever it is (party members always see each other,
/// R-2.4-03).</item>
/// <item>Whisper: the one recipient, and the echo to the sender carries the recipient's name as the sender sees it.</item>
/// </list>
/// The sender's name comes from <see cref="DisplayIdentity"/> per recipient (rule 8). Refusals go to their sender as
/// <see cref="ChatRefusedMessage"/>.
/// <para>
/// Local candidates come from the spatial grid around the sender: interest is a square of half-size <c>interestRadiusCm</c>
/// around each observer, so anyone who has the sender in interest stands within that distance of it, plus one grid cell
/// for movement since the last interest update. Reused buffers and message; sending allocates nothing.
/// </para>
/// </summary>
internal sealed class ChatSender
{
    // A cached Comparison: List.Sort(IComparer) would allocate a delegate on every call.
    private static readonly Comparison<Entity> ById = (a, b) => a.Id.CompareTo(b.Id);

    private readonly ITransport _transport;
    private readonly SpatialGrid _grid;
    private readonly int _reachCm;
    private readonly ReliableMessageWriter _writer = new(ChatCodec.MaxLineSize);
    private readonly ChatLineMessage _line = new();
    private readonly ChatRefusedMessage _refused = new();
    private readonly List<Entity> _candidates = new();
    private readonly long[] _members = new long[ReliableMessages.MaxPartyMembers];

    public ChatSender(ITransport transport, SpatialGrid grid, int interestRadiusCm)
    {
        _transport = transport;
        _grid = grid;
        _reachCm = interestRadiusCm + grid.CellCm;
    }

    /// <summary>The parties a party line reaches (WP-M4-2).</summary>
    public IPartyQuery Parties { get; set; } = NoParties.Instance;

    /// <summary>The world's player of a character; null when it is not in the world (WP-M4-2).</summary>
    public Func<long, Player?> PlayerOf { get; set; } = _ => null;

    /// <summary>Chat lines sent to clients, echoes included (telemetry <c>ChatCounters</c>).</summary>
    public long Deliveries { get; private set; }

    public void Send(ChatService chat)
    {
        foreach (var refusal in chat.Refusals)
        {
            if (!refusal.Sender.IsConnected)
                continue;
            _refused.Reason = refusal.Reason;
            _transport.Send(refusal.Sender.PeerId, Channel.ReliableOrdered, ChatCodec.WriteRefused(_writer, _refused));
        }
        foreach (var line in chat.Lines)
        {
            switch (line.Channel)
            {
                case ChatChannel.Party:
                    DeliverToParty(line);
                    break;
                case ChatChannel.Whisper:
                    DeliverWhisper(line);
                    break;
                default:
                    DeliverLocal(line);
                    break;
            }
        }
        chat.Clear();
    }

    private void DeliverLocal(in PendingChatLine line)
    {
        var sender = line.Sender;
        int x = sender.XCm, y = sender.YCm;
        _candidates.Clear();
        _grid.QueryCells(x - _reachCm, y - _reachCm, x + _reachCm, y + _reachCm, _candidates);
        _candidates.Sort(ById);
        foreach (var candidate in _candidates)
        {
            if (candidate is not Player { IsConnected: true } recipient)
                continue;
            if (recipient != sender && !recipient.Interest.Contains(sender))
                continue;
            SendLine(line, recipient, string.Empty);
        }
    }

    private void DeliverToParty(in PendingChatLine line)
    {
        long partyId = Parties.PartyIdOf(line.Sender.CharacterId);
        int count = Parties.MembersOf(partyId, _members);
        _candidates.Clear();
        for (int i = 0; i < count; i++)
        {
            if (PlayerOf(_members[i]) is { IsConnected: true } member)
                _candidates.Add(member);
        }
        _candidates.Sort(ById);
        foreach (var candidate in _candidates)
            SendLine(line, (Player)candidate, string.Empty);
    }

    private void DeliverWhisper(in PendingChatLine line)
    {
        if (line.Recipient is not { IsConnected: true } recipient)
            return; // it left between phase 1 and phase 10 of one tick: nothing to deliver
        SendLine(line, recipient, string.Empty);
        if (line.Sender.IsConnected)
            SendLine(line, line.Sender, DisplayIdentity.For(recipient, line.Sender));
    }

    private void SendLine(in PendingChatLine line, Player recipient, string targetName)
    {
        var name = DisplayIdentity.For(line.Sender, recipient);
        if (name.Length == 0)
            return;
        _line.SenderEntityId = line.Sender.Id;
        _line.SenderName = name;
        _line.Text = line.Text;
        _line.Channel = line.Channel;
        _line.TargetName = targetName;
        _transport.Send(recipient.PeerId, Channel.ReliableOrdered, ChatCodec.WriteLine(_writer, _line));
        Deliveries++;
    }
}
