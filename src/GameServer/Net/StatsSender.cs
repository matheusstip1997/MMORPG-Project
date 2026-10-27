using GameServer.Entities;
using Shared.Protocol;

namespace GameServer.Net;

/// <summary>
/// Phase 10 (WP-M2-2, spec 9.4): a connected player's own final stats, attributes, toxicity and satiety
/// (<see cref="OwnStats"/>), when they changed or its client is new; never per tick otherwise. Reliable channel,
/// MemoryPack. One reused message, value array and writer, so a send allocates nothing once the buffers have grown.
/// </summary>
internal sealed class StatsSender
{
    private readonly ITransport _transport;
    private readonly ReliableMessageWriter _writer = new();
    private readonly OwnStats _message = new();

    public StatsSender(ITransport transport)
    {
        _transport = transport;
    }

    public long Sent { get; private set; }

    /// <param name="utcNowMs">UTC time from <c>IClock</c>, the meters' clock.</param>
    public void Send(Player player, long utcNowMs)
    {
        if (!player.StatsMessageDue || !player.IsConnected || player.Combatant is not { } combatant)
            return;
        player.StatsMessageDue = false;
        var stats = combatant.Stats;
        int count = stats.Rules.Count;
        if (_message.Values.Length != count)
            _message.Values = new int[count];
        for (int stat = 0; stat < count; stat++)
            _message.Values[stat] = stats.Get(stat);
        _message.Version = (uint)stats.Version;
        var meters = player.Consumption;
        _message.ToxicityMilli = meters == null ? 0 : (int)Math.Min(int.MaxValue, meters.ToxicityMilli(utcNowMs));
        _message.SatietyMilli = meters == null ? 0 : (int)Math.Min(int.MaxValue, meters.SatietyMilli(utcNowMs));
        _transport.Send(player.PeerId, Channel.ReliableOrdered, StatsCodec.Write(_writer, _message));
        Sent++;
    }
}
