using System.Diagnostics;

namespace GameServer.Dev;

/// <summary>
/// Watches the config tables directory for the dev hot reload (spec 11.7) and calls <c>onQuiet</c> once no table file has
/// changed for <c>DevTools.hotReloadDebounceMs</c> (editors and git write in bursts). Its own background thread runs the
/// callback, so reading and checking the tables never touches the tick thread. Debounce time is wall time, not simulation
/// time.
/// </summary>
public sealed class TableWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly int _debounceMs;
    private readonly Action _onQuiet;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _changed = new(false);
    private readonly CancellationTokenSource _stop = new();
    private long _lastChangeTicks;

    public TableWatcher(string directory, int debounceMs, Action onQuiet)
    {
        if (debounceMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(debounceMs));
        _debounceMs = debounceMs;
        _onQuiet = onQuiet;
        _watcher = new FileSystemWatcher(Path.GetFullPath(directory), "*.json")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
            IncludeSubdirectories = false,
        };
        _watcher.Changed += OnChanged;
        _watcher.Created += OnChanged;
        _watcher.Deleted += OnChanged;
        _watcher.Renamed += (_, _) => Signal();
        _thread = new Thread(Run) { IsBackground = true, Name = "Table hot reload" };
        _thread.Start();
        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>Reloads that threw (a bug: the reloader reports bad tables itself).</summary>
    public int Failures { get; private set; }

    private void OnChanged(object sender, FileSystemEventArgs e) => Signal();

    private void Signal()
    {
        Interlocked.Exchange(ref _lastChangeTicks, Stopwatch.GetTimestamp());
        _changed.Set();
    }

    private void Run()
    {
        var token = _stop.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                _changed.Wait(token);
                _changed.Reset();
                // Wait until the burst is over: no event for the whole debounce time.
                while (true)
                {
                    long quietMs = (long)Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastChangeTicks)).TotalMilliseconds;
                    if (quietMs >= _debounceMs)
                        break;
                    if (token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(_debounceMs - quietMs)))
                        return;
                }
                _changed.Reset();
                try
                {
                    _onQuiet();
                }
                catch (Exception e)
                {
                    Failures++;
                    Console.Error.WriteLine($"ERROR table hot reload failed: {e}");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _stop.Cancel();
        _thread.Join();
        _stop.Dispose();
        _changed.Dispose();
    }
}
