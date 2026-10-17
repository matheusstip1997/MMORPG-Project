namespace GameServer.Net;

/// <summary>
/// The network thread (spec 1.6): polls the transport, whose listener callbacks run here and only enqueue.
/// Waits <c>ServerLoop.NetPollIntervalMs</c> between polls.
/// </summary>
public sealed class NetworkPump : IDisposable
{
    private readonly ITransport _transport;
    private readonly int _pollIntervalMs;
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _thread;

    public NetworkPump(ITransport transport, int pollIntervalMs)
    {
        if (pollIntervalMs < 1)
            throw new ArgumentOutOfRangeException(nameof(pollIntervalMs), "must be at least 1");
        _transport = transport;
        _pollIntervalMs = pollIntervalMs;
        _thread = new Thread(Run) { Name = "Network poll", IsBackground = true };
    }

    public void Start() => _thread.Start();

    public void Stop()
    {
        _stop.Cancel();
        if (_thread.IsAlive)
            _thread.Join();
    }

    public void Dispose()
    {
        Stop();
        _stop.Dispose();
    }

    private void Run()
    {
        var token = _stop.Token;
        while (!token.IsCancellationRequested)
        {
            _transport.PollEvents();
            token.WaitHandle.WaitOne(_pollIntervalMs);
        }
    }
}
