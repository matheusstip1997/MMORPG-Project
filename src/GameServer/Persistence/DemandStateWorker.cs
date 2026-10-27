using System.Threading.Channels;
using GameServer.Economy;

namespace GameServer.Persistence;

/// <summary>Where the world hands its demand state to be saved. Must never block the calling (tick) thread.</summary>
public interface IDemandStateSink
{
    void Save(IReadOnlyList<DemandStateRow> rows);
}

/// <summary>
/// Saves the demand loop's state off the world thread (spec 1.6; WP-M2-4). The world hands it a copy every
/// <c>DemandSettings.persistIntervalMs</c>; one background loop writes the newest copy (older ones still queued are
/// skipped, they are superseded). A failed write is counted and the next interval writes again: the state is volatile
/// (R-7.1-01 class 3) and moves no value.
/// </summary>
public sealed class DemandStateWorker : IDemandStateSink, IAsyncDisposable
{
    private readonly DemandStateRepository _repository;
    private readonly int _worldId;
    private readonly Channel<IReadOnlyList<DemandStateRow>> _saves =
        Channel.CreateUnbounded<IReadOnlyList<DemandStateRow>>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _loop;
    private long _failures;
    private long _saved;

    public DemandStateWorker(DemandStateRepository repository, int worldId)
    {
        _repository = repository;
        _worldId = worldId;
        _loop = Task.Run(RunAsync);
    }

    public long Failures => Interlocked.Read(ref _failures);
    public long Saved => Interlocked.Read(ref _saved);

    public void Save(IReadOnlyList<DemandStateRow> rows) => _saves.Writer.TryWrite(rows);

    /// <summary>Stops taking saves, writes the newest one still queued, then stops.</summary>
    public async ValueTask DisposeAsync()
    {
        _saves.Writer.TryComplete();
        await _loop;
    }

    private async Task RunAsync()
    {
        while (await _saves.Reader.WaitToReadAsync())
        {
            IReadOnlyList<DemandStateRow>? newest = null;
            while (_saves.Reader.TryRead(out var rows))
                newest = rows;
            if (newest == null)
                continue;
            try
            {
                await _repository.SaveAsync(_worldId, newest);
                Interlocked.Increment(ref _saved);
            }
            catch (Exception)
            {
                Interlocked.Increment(ref _failures);
            }
        }
    }
}
