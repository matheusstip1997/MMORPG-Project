using Shared.Util;

namespace GameServer.Tests.Support;

public sealed class FakeClock(long utcNowMs = 1_800_000_000_000) : IClock
{
    public long UtcNowMs { get; private set; } = utcNowMs;

    public void Advance(long ms) => UtcNowMs += ms;
}
