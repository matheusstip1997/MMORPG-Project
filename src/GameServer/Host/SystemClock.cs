using Shared.Util;

namespace GameServer.Host;

/// <summary>The one place that reads the system clock (architecture rule 6).</summary>
public sealed class SystemClock : IClock
{
    public long UtcNowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
