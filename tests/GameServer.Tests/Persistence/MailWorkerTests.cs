using GameServer.Persistence;
using GameServer.Tests.Support;
using Shared.Data.Tables;

namespace GameServer.Tests.Persistence;

/// <summary>
/// The mail worker (WP-M4-3, review of PR #171): a failing store is counted and reported, once when the reads or the expiry
/// start failing and once when they work again; a failed read is answered as failed so the world answers from memory.
/// </summary>
public class MailWorkerTests
{
    private sealed class FlakyStore : IMailStore
    {
        public bool Fail { get; set; } = true;

        public Task<MailRecord> LoadOpenAsync(long characterId, int maxRows, CancellationToken ct = default) =>
            Fail ? throw new TimeoutException("database unreachable") : Task.FromResult(MailRecord.Empty);

        public Task<IReadOnlyList<MailExpiredRow>> ExpireDueAsync(long graceMs, int max, CancellationToken ct = default) =>
            Fail ? throw new TimeoutException("database unreachable") : Task.FromResult<IReadOnlyList<MailExpiredRow>>([]);
    }

    private static MailSettings Settings() => MailSettings.Find(TestCombat.Tables())!;

    [Fact]
    public async Task A_failing_expiry_is_counted_and_logged_once_until_it_works_again()
    {
        var store = new FlakyStore();
        var lines = new List<string>();
        await using var worker = new MailWorker(store, Settings(), runExpiry: false, log: lines.Add);

        Assert.Equal(-1, await worker.TryExpireAsync());
        Assert.Equal(-1, await worker.TryExpireAsync());
        Assert.Equal(2, worker.Failures);
        Assert.Contains("database unreachable", Assert.Single(lines));

        store.Fail = false;
        Assert.Equal(0, await worker.TryExpireAsync());
        Assert.Equal(2, lines.Count);
        Assert.Contains("works again", lines[^1]);
    }

    [Fact]
    public async Task A_failed_read_is_answered_as_failed_and_logged()
    {
        var lines = new List<string>();
        await using var worker = new MailWorker(new FlakyStore(), Settings(), runExpiry: false, log: line => { lock (lines) lines.Add(line); });

        worker.RequestReload(7, 3);
        MailReloaded reloaded = default;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!worker.TryTakeReloaded(out reloaded) && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.Equal((7L, 3, (MailRecord?)null), (reloaded.CharacterId, reloaded.Version, reloaded.Record));
        Assert.Equal(1, worker.Failures);
        lock (lines)
            Assert.Contains("Mailbox read failed", Assert.Single(lines));
    }
}
