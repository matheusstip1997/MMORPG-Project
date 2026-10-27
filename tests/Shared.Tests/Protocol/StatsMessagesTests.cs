using Shared.Protocol;

namespace Shared.Tests.Protocol;

/// <summary>The own-stats message (protocol 19, WP-M2-2, spec 9.4): round trip and the checks of its reader.</summary>
public class StatsMessagesTests
{
    [Fact]
    public void Own_stats_round_trip_through_the_guarded_reader()
    {
        var sent = new OwnStats { Version = 7, Values = new[] { 20, -5, 0, int.MaxValue }, ToxicityMilli = 12_500, SatietyMilli = 0 };
        var bytes = StatsCodec.Write(new ReliableMessageWriter(), sent).ToArray();

        Assert.Equal((byte)ReliableMessageId.OwnStats, bytes[0]);
        Assert.True(StatsCodec.TryRead(bytes, out var read));
        Assert.Equal(7u, read.Version);
        Assert.Equal(sent.Values, read.Values);
        Assert.Equal(12_500, read.ToxicityMilli);
        Assert.Equal(0, read.SatietyMilli);
    }

    [Fact]
    public void Negative_meters_and_too_many_stats_are_refused()
    {
        var writer = new ReliableMessageWriter();
        Assert.False(StatsCodec.TryRead(StatsCodec.Write(writer, new OwnStats { ToxicityMilli = -1 }).ToArray(), out _));
        Assert.False(StatsCodec.TryRead(StatsCodec.Write(writer, new OwnStats { SatietyMilli = -1 }).ToArray(), out _));
        var tooMany = StatsCodec.Write(writer, new OwnStats { Values = new int[StatsCodec.MaxStatCount + 1] }).ToArray();
        Assert.False(ReliableMessageReader.IsWellFormed(tooMany, int.MaxValue)); // the guard's array bound
        Assert.False(StatsCodec.TryRead(tooMany, out _));
    }

    [Fact]
    public void It_is_a_server_message_with_no_client_cap()
    {
        Assert.True(ReliableMessageReader.TryGetSpec(ReliableMessageId.OwnStats, out var spec));
        Assert.Equal(ReliableDirection.ServerToClient, spec.Direction);
        Assert.Equal(typeof(OwnStats), spec.BodyType);
    }

    [Fact]
    public void OwnStats_came_with_protocol_19_as_reliable_id_21()
    {
        // WP-M2-2 took main + 1 at merge (spec 1.7) and the next free reliable id.
        Assert.True(ProtocolVersion.Current >= 19);
        Assert.Equal(21, (int)ReliableMessageId.OwnStats);
    }
}
