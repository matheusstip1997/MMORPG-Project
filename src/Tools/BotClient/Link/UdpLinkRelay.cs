using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Shared.Data.Tables;
using Shared.Util;

namespace BotClient.Link;

/// <summary>What a relay did so far.</summary>
public readonly record struct RelayStats(
    int Clients,
    long UpAdmitted,
    long UpDropped,
    long DownAdmitted,
    long DownDropped,
    long Sent,
    long MaxLateUs,
    long TotalLateUs,
    bool SharedSourceAddress)
{
    /// <summary>Mean time datagrams left after their due time (timer and thread wake-up cost), in µs.</summary>
    public long MeanLateUs => Sent > 0 ? TotalLateUs / Sent : 0;
}

/// <summary>
/// Simulated latency, jitter and loss below LiteNetLib (backlog J, spec 11.5): a UDP relay between clients and a game
/// server that delays and drops whole datagrams per <see cref="BotLinkProfile"/>. Because it works on datagrams,
/// LiteNetLib's own ping (the round trip the server caps a client's claimed view lag with, R-3.1-03) and its resends see
/// the simulated link exactly as they would see a real one; an <c>ITransport</c> wrapper above LiteNetLib would not.
/// <list type="bullet">
/// <item>Each client endpoint gets its own upstream socket bound to its own loopback source address (127.0.0.2, .3, ...),
/// so the server's per-IP connect limit (R-2.5-03, <c>NetSecurity</c>) treats every bot as its own player. Where that
/// address cannot be bound the relay falls back to 127.0.0.1 and says so (<see cref="RelayStats.SharedSourceAddress"/>).</item>
/// <item>Each direction of each client has its own <see cref="LinkConditioner"/> with a seed derived from the relay's
/// seed and the client's index (architecture rule 6: the caller logs the seed).</item>
/// <item>One pump thread sends what is due; datagrams due on arrival (delay 0) go out at once.</item>
/// </list>
/// The Unity client can use it unchanged: run <c>BotClient relay</c> and connect to the relay's port.
/// </summary>
public sealed class UdpLinkRelay : IDisposable
{
    private const int MaxDatagramBytes = 65_507;

    private readonly IPEndPoint _server;
    private readonly BotLinkProfile _profile;
    private readonly ulong _seed;
    private readonly uint _sourceBase;
    private readonly Action<string> _log;
    private readonly Socket _listen;
    private readonly object _gate = new();
    private readonly Dictionary<IPEndPoint, ClientLink> _clients = new();
    private readonly DelayQueue<ClientLink> _up = new();
    private readonly DelayQueue<ClientLink> _down = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _pump;
    private long _sent;
    private long _maxLateUs;
    private long _totalLateUs;
    private bool _sharedSource;
    private bool _disposed;

    /// <param name="listen">Where clients send to (port 0 picks one, see <see cref="LocalEndPoint"/>).</param>
    /// <param name="server">The game server.</param>
    /// <param name="sourceBase">The first upstream source address; null for 127.0.0.2.</param>
    public UdpLinkRelay(IPEndPoint listen, IPEndPoint server, BotLinkProfile profile, ulong seed,
        IPAddress? sourceBase = null, Action<string>? log = null)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _seed = seed;
        _sourceBase = ToUInt(sourceBase ?? IPAddress.Parse("127.0.0.2"));
        _log = log ?? (_ => { });
        _listen = NewSocket();
        _listen.Bind(listen);
        // Above the bots' and LiteNetLib's threads on the same machine, so a due datagram is not held up behind them.
        _pump = new Thread(Pump) { IsBackground = true, Name = "link-relay-pump", Priority = ThreadPriority.AboveNormal };
    }

    public IPEndPoint LocalEndPoint => (IPEndPoint)_listen.LocalEndPoint!;

    public BotLinkProfile Profile => _profile;

    public void Start()
    {
        _pump.Start();
        _ = ReceiveFromClientsAsync();
    }

    public RelayStats Stats()
    {
        lock (_gate)
        {
            long upAdmitted = 0, upDropped = 0, downAdmitted = 0, downDropped = 0;
            foreach (var client in _clients.Values)
            {
                upAdmitted += client.Up.Admitted;
                upDropped += client.Up.Dropped;
                downAdmitted += client.Down.Admitted;
                downDropped += client.Down.Dropped;
            }
            return new RelayStats(_clients.Count, upAdmitted, upDropped, downAdmitted, downDropped, _sent, _maxLateUs,
                _totalLateUs, _sharedSource);
        }
    }

    /// <summary>The source address the relay uses toward the server for the client at <paramref name="clientEndPoint"/>.</summary>
    public IPEndPoint? UpstreamEndPointOf(IPEndPoint clientEndPoint)
    {
        lock (_gate)
            return _clients.TryGetValue(clientEndPoint, out var link) ? (IPEndPoint)link.Socket.LocalEndPoint! : null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }
        _stop.Cancel();
        _wake.Set();
        if (_pump.IsAlive)
            _pump.Join(TimeSpan.FromSeconds(2));
        _listen.Dispose();
        lock (_gate)
        {
            foreach (var client in _clients.Values)
                client.Socket.Dispose();
            _up.Clear();
            _down.Clear();
        }
        _wake.Dispose();
        _stop.Dispose();
    }

    private async Task ReceiveFromClientsAsync()
    {
        var buffer = new byte[MaxDatagramBytes];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (!_stop.IsCancellationRequested)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await _listen.ReceiveFromAsync(buffer, SocketFlags.None, any, _stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                continue; // a client went away (ICMP unreachable); the others carry on
            }

            var from = (IPEndPoint)result.RemoteEndPoint;
            var link = LinkFor(from);
            if (link != null)
                Admit(link, up: true, buffer.AsSpan(0, result.ReceivedBytes));
        }
    }

    private async Task ReceiveFromServerAsync(ClientLink link)
    {
        var buffer = new byte[MaxDatagramBytes];
        while (!_stop.IsCancellationRequested)
        {
            int received;
            try
            {
                received = await link.Socket.ReceiveAsync(buffer, SocketFlags.None, _stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                continue; // the server is not up (yet); LiteNetLib retries
            }
            Admit(link, up: false, buffer.AsSpan(0, received));
        }
    }

    private ClientLink? LinkFor(IPEndPoint from)
    {
        lock (_gate)
        {
            if (_disposed)
                return null;
            if (_clients.TryGetValue(from, out var link))
                return link;

            int index = _clients.Count;
            var socket = NewSocket();
            var source = FromUInt(_sourceBase + (uint)index);
            try
            {
                socket.Bind(new IPEndPoint(source, 0));
            }
            catch (SocketException)
            {
                if (!_sharedSource)
                    _log($"link relay: cannot bind {source}; clients share 127.0.0.1 toward the server (per-IP limits apply to all)");
                _sharedSource = true;
                socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            }
            socket.Connect(_server);
            // Streams 2i and 2i+1 of the relay seed: each direction of each client is independent and reproducible.
            link = new ClientLink(from, socket,
                LinkConditioner.Up(_profile, new Pcg32Rng(_seed, (ulong)index * 2)),
                LinkConditioner.Down(_profile, new Pcg32Rng(_seed, (ulong)index * 2 + 1)));
            _clients.Add(from, link);
            _ = ReceiveFromServerAsync(link);
            return link;
        }
    }

    private void Admit(ClientLink link, bool up, ReadOnlySpan<byte> data)
    {
        long now = NowUs();
        lock (_gate)
        {
            if (_disposed)
                return;
            var conditioner = up ? link.Up : link.Down;
            if (!conditioner.Admit(now, out long due))
                return;
            var queue = up ? _up : _down;
            // Due on arrival with nothing ahead of it in this direction: no queue hop.
            if (due <= now && queue.Count == 0)
            {
                Send(link, up, data, 0);
                return;
            }
            queue.Enqueue(due, data, link);
        }
        _wake.Set();
    }

    private void Pump()
    {
        while (!_stop.IsCancellationRequested)
        {
            long waitUs;
            lock (_gate)
            {
                long now = NowUs();
                SendDue(_up, up: true, now);
                SendDue(_down, up: false, now);
                waitUs = long.MaxValue;
                if (_up.TryPeekDue(out long upDue))
                    waitUs = upDue - now;
                if (_down.TryPeekDue(out long downDue))
                    waitUs = Math.Min(waitUs, downDue - now);
            }
            if (waitUs <= 0)
                continue;
            // Millisecond waits; Program raises the Windows timer resolution so they are about 1 ms (spec 11.5 notes).
            int waitMs = waitUs == long.MaxValue ? 100 : (int)Math.Clamp(waitUs / 1_000, 1, 100);
            _wake.WaitOne(waitMs);
        }
    }

    private void SendDue(DelayQueue<ClientLink> queue, bool up, long now)
    {
        while (queue.TryDequeueDue(now, out var datagram))
        {
            Send(datagram.Target, up, datagram.Data, now - datagram.DueUs);
            DelayQueue<ClientLink>.Return(datagram);
        }
    }

    private void Send(ClientLink link, bool up, ReadOnlySpan<byte> data, long lateUs)
    {
        try
        {
            if (up)
                link.Socket.Send(data);
            else
                _listen.SendTo(data, SocketFlags.None, link.Client);
        }
        catch (SocketException)
        {
            return; // like a lost datagram
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        _sent++;
        _totalLateUs += lateUs;
        if (lateUs > _maxLateUs)
            _maxLateUs = lateUs;
    }

    private static long NowUs() => Stopwatch.GetTimestamp() * 1_000_000 / Stopwatch.Frequency;

    private static Socket NewSocket()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        if (OperatingSystem.IsWindows())
        {
            // Windows reports an earlier ICMP "port unreachable" as a reset on the next receive; ignore it (as LiteNetLib does).
            const int SioUdpConnReset = -1744830452;
            socket.IOControl(SioUdpConnReset, [0], null);
        }
        return socket;
    }

    private static uint ToUInt(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);
    }

    private static IPAddress FromUInt(uint value) =>
        new([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);

    private sealed class ClientLink(IPEndPoint client, Socket socket, LinkConditioner up, LinkConditioner down)
    {
        public IPEndPoint Client { get; } = client;
        public Socket Socket { get; } = socket;
        public LinkConditioner Up { get; } = up;
        public LinkConditioner Down { get; } = down;
    }
}
