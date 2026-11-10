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
/// expired mails are published (<c>MailExpired</c>, a sink). A failed run is counted and logged (one line when the reads or the
/// expiry start failing, one when they work again, so a dead database is reported without flooding the log), and the next
/// interval runs again.
/// <para>
/// The reload queue is unbounded in type but bounded in use: the world asks for at most one read per attached character at a
/// time (<c>GameWorld</c> keeps the characters with a read in flight and asks again only after the answer), so it never holds
/// more reads than the world has characters.
/// </para>
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
    private readonly Action<string> _log;
    private long _failures;
    private long _expiredTotal;
    private bool _reloadFailing;
    private bool _expiryFailing;

    /// <param name="runExpiry">False in tests that drive <see cref="TryExpireAsync"/> themselves.</param>
    /// <param name="log">Receives a line when the reads or the expiry start failing and when they recover; default standard error.</param>
    public MailWorker(IMailStore store, MailSettings settings, bool runExpiry = true, Action<string>? log = null)
    {
        _store = store;
        _settings = settings;
        _log = log ?? Console.Error.WriteLine;
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

    /// <summary>
    /// One expiry pass (the loop's body; tests call it): runs until a run closes fewer than a batch. Returns the mails closed, or
    /// -1 when the store failed (counted, and logged once until a pass works again).
    /// </summary>
    public async Task<int> TryExpireAsync(CancellationToken ct = default)
    {
        int total = 0;
        try
        {
            int closed;
            do
            {
                closed = await RunExpiryOnceAsync(ct).ConfigureAwait(false);
                total += closed;
            }
            while (closed == _settings.ExpiryBatchSize);
            if (_expiryFailing)
                _log("Mail expiry works again.");
            _expiryFailing = false;
            return total;
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Interlocked.Increment(ref _failures);
            if (!_expiryFailing)
                _log($"Mail expiry failed (open mails do not expire until it works again): {e.Message}");
            _expiryFailing = true;
            return -1;
        }
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
        var token = _stop.Token;
        await foreach (var (characterId, version) in _reloads.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            MailRecord? record = null;
            if (!token.IsCancellationRequested) // at shutdown the queued reads are answered as failed, not run
            {
                try
                {
                    record = await _store.LoadOpenAsync(characterId, _settings.MaxListed, token).ConfigureAwait(false);
                    if (_reloadFailing)
                        _log("Mailbox reads work again.");
                    _reloadFailing = false;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                }
                catch (Exception e)
                {
                    Interlocked.Increment(ref _failures);
                    if (!_reloadFailing)
                        _log($"Mailbox read failed (lists answer from memory until it works again): {e.Message}");
                    _reloadFailing = true;
                }
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
            await TryExpireAsync(token).ConfigureAwait(false); // a full batch runs again at once until a run closes fewer
        }
    }
}
