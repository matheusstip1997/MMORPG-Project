using GameServer.Entities;
using Shared.Protocol;

namespace GameServer.Net;

/// <summary>
/// Sends a player's refused actions (<see cref="ActionResult"/>, reason codes of spec 9.7) on the reliable channel in
/// tick phase 10 (spec 1.7). Every tick, not only on snapshot ticks, so the failure toast is not delayed. One reused
/// buffer; sending allocates nothing.
/// </summary>
internal sealed class ActionResultSender
{
    private readonly ITransport _transport;
    private readonly byte[] _buffer = new byte[ActionResult.Size];

    public ActionResultSender(ITransport transport)
    {
        _transport = transport;
    }

    public long Sent { get; private set; }

    public void Send(Player player)
    {
        var results = player.ActionResults;
        for (int i = 0; i < results.Count; i++)
        {
            var result = results[i];
            ActionResult.Write(_buffer, result.InputSequence, result.ActionNetId, result.SkillSlot, result.Reason);
            _transport.Send(player.PeerId, Channel.ReliableOrdered, _buffer);
            Sent++;
        }
        results.Clear();
    }
}
