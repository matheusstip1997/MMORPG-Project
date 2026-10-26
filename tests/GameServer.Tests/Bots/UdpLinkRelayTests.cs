using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using BotClient.Link;
using Shared.Data.Tables;

namespace GameServer.Tests.Bots;

/// <summary>Backlog J: the UDP relay over real loopback sockets, between plain UDP "clients" and an echo "server".</summary>
public sealed class UdpLinkRelayTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly UdpClient _server = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly List<IPEndPoint> _serverSaw = [];
    private readonly CancellationTokenSource _stop = new();

    public UdpLinkRelayTests()
    {
        // The echo server sends every datagram straight back to where it came from (the relay's upstream socket).
        _ = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                UdpReceiveResult received;
                try
                {
                    received = await _server.ReceiveAsync(_stop.Token);
                }
                catch (Exception)
                {
                    return;
                }
                lock (_serverSaw)
                    _serverSaw.Add(received.RemoteEndPoint);
                await _server.SendAsync(received.Buffer, received.RemoteEndPoint);
            }
        });
    }

    private IPEndPoint Server => (IPEndPoint)_server.Client.LocalEndPoint!;

    [Fact]
    public async Task Datagrams_cross_both_ways_after_the_round_trip_of_the_profile()
    {
        var profile = new BotLinkProfile { Id = "rtt100", UpDelayMs = 50, DownDelayMs = 50 };
        using var relay = new UdpLinkRelay(new IPEndPoint(IPAddress.Loopback, 0), Server, profile, seed: 1);
        relay.Start();
        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));

        var roundTrips = new List<double>();
        for (int i = 0; i < 5; i++)
        {
            var watch = Stopwatch.StartNew();
            await client.SendAsync(new byte[] { (byte)i, 1, 2, 3 }, relay.LocalEndPoint);
            var echo = await ReceiveAsync(client);
            roundTrips.Add(watch.Elapsed.TotalMilliseconds);
            Assert.Equal(new byte[] { (byte)i, 1, 2, 3 }, echo);
        }

        // At least the simulated 100 ms; the relay's wake-ups add a few ms (Windows timer, loaded CI machines more).
        Assert.All(roundTrips, ms => Assert.InRange(ms, 99.0, 250.0));
        var stats = relay.Stats();
        Assert.Equal((1, 5L, 5L, 10L), (stats.Clients, stats.UpAdmitted, stats.DownAdmitted, stats.Sent));
    }

    [Fact]
    public async Task Full_loss_delivers_nothing()
    {
        var profile = new BotLinkProfile { Id = "dead", LossPercent = 100 };
        using var relay = new UdpLinkRelay(new IPEndPoint(IPAddress.Loopback, 0), Server, profile, seed: 1);
        relay.Start();
        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));

        for (int i = 0; i < 20; i++)
            await client.SendAsync(new byte[] { 1 }, relay.LocalEndPoint);
        await Task.Delay(200);

        lock (_serverSaw)
            Assert.Empty(_serverSaw);
        Assert.Equal(20, relay.Stats().UpDropped);
    }

    [Fact]
    public async Task Each_client_reaches_the_server_from_its_own_loopback_address()
    {
        // R-2.5-03: the server limits connection attempts per IP, so bots must not all come from 127.0.0.1.
        var profile = new BotLinkProfile { Id = "lan" };
        using var relay = new UdpLinkRelay(new IPEndPoint(IPAddress.Loopback, 0), Server, profile, seed: 1);
        relay.Start();
        using var a = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var b = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));

        await a.SendAsync(new byte[] { 1 }, relay.LocalEndPoint);
        Assert.Equal(new byte[] { 1 }, await ReceiveAsync(a));
        await b.SendAsync(new byte[] { 2 }, relay.LocalEndPoint);
        Assert.Equal(new byte[] { 2 }, await ReceiveAsync(b));

        List<IPAddress> addresses;
        lock (_serverSaw)
            addresses = _serverSaw.Select(e => e.Address).Distinct().ToList();
        Assert.False(relay.Stats().SharedSourceAddress, "binding 127.0.0.x works on Windows and Linux");
        Assert.Equal([IPAddress.Parse("127.0.0.2"), IPAddress.Parse("127.0.0.3")], addresses);
    }

    private static async Task<byte[]> ReceiveAsync(UdpClient client)
    {
        using var cancel = new CancellationTokenSource(Timeout);
        return (await client.ReceiveAsync(cancel.Token)).Buffer;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _server.Dispose();
        _stop.Dispose();
    }
}
