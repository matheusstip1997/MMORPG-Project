using GameServer.Entities;
using GameServer.Social;
using Shared.Protocol;

namespace GameServer.Net;

/// <summary>
/// Sends an observer the display name of every player that entered its interest at the last interest update, itself
/// included (its own interest always holds it, and a rebind resets interest), as <see cref="EntityIdentity"/> on the
/// reliable channel (spec 9.3 name plates; protocol 10). Names come from <see cref="DisplayIdentity"/> (rule 8), and
/// interest already applies the visibility rule, so a hidden player's name is never sent (rule 9). Runs in tick phase 10
/// on snapshot ticks, before the snapshots. One reused buffer; sending allocates nothing.
/// </summary>
internal sealed class IdentitySender
{
    private readonly ITransport _transport;
    private readonly byte[] _buffer = new byte[EntityIdentity.MaxSize];

    public IdentitySender(ITransport transport)
    {
        _transport = transport;
    }

    public long Sent { get; private set; }

    public void Send(Player observer)
    {
        var entered = observer.Interest.Entered;
        for (int i = 0; i < entered.Count; i++)
        {
            if (entered[i] is not Player)
                continue;
            var name = DisplayIdentity.For(entered[i], observer);
            if (name.Length == 0)
                continue;
            int length = EntityIdentity.Write(_buffer, entered[i].Id, name);
            _transport.Send(observer.PeerId, Channel.ReliableOrdered, _buffer.AsSpan(0, length));
            Sent++;
        }
    }
}
