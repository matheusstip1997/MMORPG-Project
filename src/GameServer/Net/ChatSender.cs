using GameServer.Entities;
using GameServer.Social;
using GameServer.World;
using Shared.Protocol;

namespace GameServer.Net;

/// <summary>
/// Delivers local chat in tick phase 10, every tick (spec 1.7: reliable channel; R-6.5-01). Each accepted line goes, as a
/// <see cref="ChatLineMessage"/>, to every connected player whose interest holds the sender, in id order, and to the
/// sender itself (the echo that shows its line was accepted). Interest already applies <c>IVisibilityQuery</c>, so a
/// player who cannot see the sender never gets its line (rule 9). The sender's name comes from
/// <see cref="DisplayIdentity"/> per recipient (rule 8). Refusals go to their sender as <see cref="ChatRefusedMessage"/>.
/// <para>
/// Candidates come from the spatial grid around the sender: interest is a square of half-size <c>interestRadiusCm</c>
/// around each observer, so anyone who has the sender in interest stands within that distance of it, plus one grid cell
/// for movement since the last interest update. One reused buffer and message; sending allocates nothing.
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

    public ChatSender(ITransport transport, SpatialGrid grid, int interestRadiusCm)
    {
        _transport = transport;
        _grid = grid;
        _reachCm = interestRadiusCm + grid.CellCm;
    }

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
            Deliver(line);
        chat.Clear();
    }

    private void Deliver(in PendingChatLine line)
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
            var name = DisplayIdentity.For(sender, recipient);
            if (name.Length == 0)
                continue;
            _line.SenderEntityId = sender.Id;
            _line.SenderName = name;
            _line.Text = line.Text;
            _transport.Send(recipient.PeerId, Channel.ReliableOrdered, ChatCodec.WriteLine(_writer, _line));
            Deliveries++;
        }
    }
}
