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
/// Two-character jobs (<see cref="TransferSaveJob"/>, WP-M4-4) ride the same loop and complete as one result per character.
/// </summary>
public sealed class PersistenceWorker : ISaveJobSink, ITransferJobSink, ISaveCompletionSource, IAsyncDisposable
{
    private readonly ICharacterSaveStore _store;
    private readonly ITransferSaveStore? _jobStore;
    private readonly Channel<Work> _jobs = Channel.CreateUnbounded<Work>(new UnboundedChannelOptions { SingleReader = true });
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
        _jobStore = store as ITransferSaveStore;
        _clock = clock;
        _telemetry = telemetry;
        _loop = Task.Run(RunAsync);
    }

    public void Enqueue(in SaveJob job)
    {
        if (!_jobs.Writer.TryWrite(new Work(job, null)))
            throw new InvalidOperationException("persistence worker is stopped");
    }

    public void EnqueueTransfer(in TransferSaveJob job)
    {
        if (_jobStore == null)
            throw new InvalidOperationException("this store cannot write a transfer between two characters");
        if (!_jobs.Writer.TryWrite(new Work(default, job)))
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
        await foreach (var work in _jobs.Reader.ReadAllAsync())
        {
            if (work.Transfer is { } transfer)
            {
                await RunTransferAsync(transfer);
                continue;
            }
            var job = work.Job;
            SaveCompletion completion;
            try
            {
                var result = _jobStore != null
                    ? await _jobStore.SaveJobAsync(job)
                    : await _store.SaveAsync(job.CharacterId, job.State, job.ExpectedVersion, job.SaveId, job.Inventory, progression: job.Progression,
                        skills: job.Skills, bystial: job.Bystial, quests: job.Quests, jobs: job.Jobs, craft: job.Craft);
                completion = SaveCompletion.From(job, result);
            }
            catch (Exception e)
            {
                // Outcome unknown (it may have committed); the scheduler retries the identical job. Telemetry only
                // enqueues, so a full queue drops the record instead of holding up saves.
                Failed(job);
                completion = new SaveCompletion(job.CharacterId, job.SaveId, SaveOutcome.Failed, 0, e);
            }

            _completions.Enqueue(completion);
        }
    }

    private async Task RunTransferAsync(TransferSaveJob job)
    {
        SaveCompletion a, b;
        try
        {
            var result = await _jobStore!.SaveTransferAsync(job);
            a = SaveCompletion.From(job.A, result.A);
            b = SaveCompletion.From(job.B, result.B);
        }
        catch (Exception e)
        {
            // Outcome unknown for both (one transaction): the scheduler resends the identical pair job.
            Failed(job.A);
            Failed(job.B);
            a = new SaveCompletion(job.A.CharacterId, job.A.SaveId, SaveOutcome.Failed, 0, e);
            b = new SaveCompletion(job.B.CharacterId, job.B.SaveId, SaveOutcome.Failed, 0, e);
        }
        _completions.Enqueue(a);
        _completions.Enqueue(b);
    }

    private void Failed(in SaveJob job) =>
        _telemetry?.TryEnqueue(new TelemetryRecord(TelemetryKind.SaveFailed, _clock!.UtcNowMs,
            CharacterId: job.CharacterId, V0: (long)job.Reason));

    private readonly record struct Work(SaveJob Job, TransferSaveJob? Transfer);
}
