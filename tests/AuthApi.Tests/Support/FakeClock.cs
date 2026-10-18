using Shared.Util;

namespace AuthApi.Tests.Support;

/// <summary>Thread-safe: concurrent requests read it while a test advances it.</summary>
public sealed class FakeClock(long utcNowMs = 1_800_000_000_000) : IClock
{
    private long _now = utcNowMs;

    public long UtcNowMs => Interlocked.Read(ref _now);

    public void Advance(long ms) => Interlocked.Add(ref _now, ms);
}
