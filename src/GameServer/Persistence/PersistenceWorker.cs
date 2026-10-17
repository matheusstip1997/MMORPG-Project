using System.Collections.Concurrent;
using System.Threading.Channels;

namespace GameServer.Persistence;

/// <summary>
/// Runs character saves off the world thread (spec 1.6). The world enqueues <see cref="SaveJob"/>s without
/// blocking; one background loop calls the store in order and puts each result on a completion queue that
/// the world drains in <see cref="SaveScheduler.Tick"/>. The tick never waits on the database.
/// One loop per world keeps jobs in order; the scheduler also allows only one save in flight per character.
/// </summary>
public sealed class PersistenceWorker : ISaveJobSink, ISaveCompletionSource, IAsyncDisposable
{
    private readonly ICharacterSaveStore _store;
    private readonly Channel<SaveJob> _jobs = Channel.CreateUnbounded<SaveJob>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentQueue<SaveCompletion> _completions = new();
    private readonly Task _loop;

    public PersistenceWorker(ICharacterSaveStore store)
    {
        _store = store;
        _loop = Task.Run(RunAsync);
    }

    public void Enqueue(in SaveJob job)
    {
        if (!_jobs.Writer.TryWrite(job))
            throw new InvalidOperationException("persistence worker is stopped");
    }

    public bool TryDequeue(out SaveCompletion completion) => _completions.TryDequeue(out completion);

    /// <summary>Stops taking jobs, finishes the ones already queued (graceful shutdown saves, R-2.1-09), then stops.</summary>
    public async ValueTask DisposeAsync()
    {
        _jobs.Writer.TryComplete();
        await _loop;
    }

    private async Task RunAsync()
    {
        await foreach (var job in _jobs.Reader.ReadAllAsync())
        {
            SaveCompletion completion;
            try
            {
                var result = await _store.SaveAsync(job.CharacterId, job.State, job.ExpectedVersion, job.SaveId);
                completion = SaveCompletion.From(job, result);
            }
            catch (Exception e)
            {
                // Outcome unknown (it may have committed); the scheduler retries the identical job.
                // TODO: report through telemetry once the H2 pipeline lands.
                completion = new SaveCompletion(job.CharacterId, job.SaveId, SaveOutcome.Failed, 0, e);
            }

            _completions.Enqueue(completion);
        }
    }
}
