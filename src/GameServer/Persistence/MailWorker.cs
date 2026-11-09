using System.Collections.Concurrent;
using System.Threading.Channels;
using Shared.Data.Tables;

namespace GameServer.Persistence;

/// <summary>What the mail worker reads and writes (<see cref="MailRepository"/>; a fake in tests).</summary>
public interface IMailStore
{
    Task<MailRecord> LoadOpenAsync(long characterId, int maxRows, CancellationToken ct = default);

    Task<IReadOnlyList<MailExpiredRow>> ExpireDueAsync(long graceMs, int max, CancellationToken ct = default);
}

/// <summary>A mailbox read the world asked for: <see cref="Record"/> null when the read failed.</summary>
public readonly record struct MailReloaded(long CharacterId, int Version, MailRecord? Record);

/// <summary>
/// The world's side of the mail worker (WP-M4-3): it asks for mailbox reads and takes their results and the expired mails in
/// phase 1. Never blocks the tick.
/// </summary>
public interface IMailJobs
{
    void RequestReload(long characterId, int version);

    bool TryTakeReloaded(out MailReloaded reloaded);

    bool TryTakeExpired(out MailExpiredRow expired);
}

/// <summary>
/// The mail work off the world thread (spec 1.6; WP-M4-3, spec 4.5): mailbox reads the world asks for (a list past the loaded
/// mails, a stand-alone send for an online recipient), and the <b>expiry job</b> every <c>MailSettings.expiryJobIntervalMs</c>,
/// which closes open mails past their expiry plus the grace and destroys their attachments in one transaction
/// (<see cref="MailRepository.ExpireDueAsync"/>, idempotent). Results wait in queues the world drains in phase 1, where the
/// expired mails are published (<c>MailExpired</c>, a sink). A failed run is counted and the next interval runs again.
/// </summary>
public sealed class MailWorker : IMailJobs, IAsyncDisposable
{
    private readonly IMailStore _store;
    private readonly MailSettings _settings;
    private readonly Channel<(long CharacterId, int Version)> _reloads =
        Channel.CreateUnbounded<(long, int)>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentQueue<MailReloaded> _reloaded = new();
    private readonly ConcurrentQueue<MailExpiredRow> _expired = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _reloadLoop;
    private readonly Task? _expiryLoop;
    private long _failures;
    private long _expiredTotal;

    /// <param name="runExpiry">False in tests that drive <see cref="RunExpiryOnceAsync"/> themselves.</param>
    public MailWorker(IMailStore store, MailSettings settings, bool runExpiry = true)
    {
        _store = store;
        _settings = settings;
        _reloadLoop = Task.Run(ReloadLoopAsync);
        if (runExpiry)
            _expiryLoop = Task.Run(ExpiryLoopAsync);
    }

    public long Failures => Interlocked.Read(ref _failures);

    /// <summary>Mails this worker expired since it started.</summary>
    public long ExpiredTotal => Interlocked.Read(ref _expiredTotal);

    public void RequestReload(long characterId, int version) => _reloads.Writer.TryWrite((characterId, version));

    public bool TryTakeReloaded(out MailReloaded reloaded) => _reloaded.TryDequeue(out reloaded);

    public bool TryTakeExpired(out MailExpiredRow expired) => _expired.TryDequeue(out expired!);

    /// <summary>One expiry run (the loop's body; tests call it directly): the mails it closed, queued for the world.</summary>
    public async Task<int> RunExpiryOnceAsync(CancellationToken ct = default)
    {
        var rows = await _store.ExpireDueAsync(_settings.ExpiryGraceMs, _settings.ExpiryBatchSize, ct).ConfigureAwait(false);
        foreach (var row in rows)
            _expired.Enqueue(row);
        Interlocked.Add(ref _expiredTotal, rows.Count);
        return rows.Count;
    }

    public async ValueTask DisposeAsync()
    {
        _reloads.Writer.TryComplete();
        _stop.Cancel();
        await _reloadLoop.ConfigureAwait(false);
        if (_expiryLoop != null)
        {
            try
            {
                await _expiryLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        _stop.Dispose();
    }

    private async Task ReloadLoopAsync()
    {
        await foreach (var (characterId, version) in _reloads.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            MailRecord? record = null;
            try
            {
                record = await _store.LoadOpenAsync(characterId, _settings.MaxListed).ConfigureAwait(false);
            }
            catch (Exception)
            {
                Interlocked.Increment(ref _failures);
            }
            _reloaded.Enqueue(new MailReloaded(characterId, version, record));
        }
    }

    private async Task ExpiryLoopAsync()
    {
        var token = _stop.Token;
        while (!token.IsCancellationRequested)
        {
            await Task.Delay(_settings.ExpiryJobIntervalMs, token).ConfigureAwait(false);
            try
            {
                // A full batch may leave more due mails: run again at once until a run closes fewer.
                while (await RunExpiryOnceAsync(token).ConfigureAwait(false) == _settings.ExpiryBatchSize)
                {
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Interlocked.Increment(ref _failures);
            }
        }
    }
}
