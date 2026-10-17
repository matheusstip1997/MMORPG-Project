using GameServer.Host;

namespace GameServer.World;

/// <summary>The simulation thread a world owns (spec 1.6): runs <see cref="GameWorld.Tick"/> from a <see cref="TickLoop"/>.</summary>
public sealed class WorldThread : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _thread;
    private Exception? _failure;

    public WorldThread(GameWorld world, ITickClock clock)
    {
        World = world;
        Loop = new TickLoop(clock, world.Settings.TickRateHz, world.Tick);
        _thread = new Thread(Run) { Name = "World simulation", IsBackground = true };
    }

    public GameWorld World { get; }

    /// <summary>Tick timing; read it after <see cref="Stop"/> or accept slightly stale values.</summary>
    public TickLoop Loop { get; }

    /// <summary>The exception that stopped the simulation thread, if any.</summary>
    public Exception? Failure => Volatile.Read(ref _failure);

    public bool IsRunning => _thread.IsAlive;

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
        try
        {
            Loop.Run(_stop.Token);
        }
        catch (Exception e)
        {
            // TODO(H2): report to telemetry. A crashed world must not take the process down silently.
            Volatile.Write(ref _failure, e);
            Console.Error.WriteLine($"World simulation stopped: {e}");
        }
    }
}
