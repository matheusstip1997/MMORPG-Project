using System.Collections.Concurrent;
using System.Threading.Channels;
using GameServer.Telemetry;
using Shared.Util;

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
    private readonly IClock? _clock;
    private readonly TelemetryQueue? _telemetry;
    private readonly Task _loop;

    /// <param name="clock">Timestamps the telemetry records; required with <paramref name="telemetry"/>.</param>
    /// <param name="telemetry">Receives a <see cref="TelemetryKind.SaveFailed"/> record for every save that threw.</param>
    public PersistenceWorker(ICharacterSaveStore store, IClock? clock = null, TelemetryQueue? telemetry = null)
    {
        if (telemetry != null && clock == null)
            throw new ArgumentNullException(nameof(clock), "Telemetry records need a clock.");
        _store = store;
        _clock = clock;
        _telemetry = telemetry;
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
                var result = await _store.SaveAsync(job.CharacterId, job.State, job.ExpectedVersion, job.SaveId, job.Inventory, progression: job.Progression, skills: job.Skills,
                    bystial: job.Bystial, quests: job.Quests, jobs: job.Jobs, craft: job.Craft);
                completion = SaveCompletion.From(job, result);
            }
            catch (Exception e)
            {
                // Outcome unknown (it may have committed); the scheduler retries the identical job. Telemetry only
                // enqueues, so a full queue drops the record instead of holding up saves.
                _telemetry?.TryEnqueue(new TelemetryRecord(TelemetryKind.SaveFailed, _clock!.UtcNowMs,
                    CharacterId: job.CharacterId, V0: (long)job.Reason));
                completion = new SaveCompletion(job.CharacterId, job.SaveId, SaveOutcome.Failed, 0, e);
            }

            _completions.Enqueue(completion);
        }
    }
}
