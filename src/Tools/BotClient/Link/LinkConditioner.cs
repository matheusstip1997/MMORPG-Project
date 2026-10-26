using Shared.Data.Tables;
using Shared.Util;

namespace BotClient.Link;

/// <summary>
/// One direction of a simulated link (backlog J, <see cref="BotLinkProfile"/>): decides per datagram whether it is lost and
/// when it is delivered. Pure and deterministic: integer microseconds and a seeded <see cref="IRng"/> (architecture rule 6:
/// the seed is logged by the caller), so the same seed and arrival times give the same losses and delivery times.
/// </summary>
public sealed class LinkConditioner
{
    private const long MicrosPerMs = 1_000;

    private readonly long _delayUs;
    private readonly int _jitterUs;
    private readonly int _lossPercent;
    private readonly bool _allowReorder;
    private readonly IRng _rng;
    private long _lastDueUs = long.MinValue;

    public LinkConditioner(int delayMs, int jitterMs, int lossPercent, bool allowReorder, IRng rng)
    {
        if (delayMs < 0 || jitterMs < 0 || lossPercent < 0 || lossPercent > 100)
            throw new ArgumentOutOfRangeException(nameof(delayMs), "Delay and jitter must not be negative; loss is 0-100 %.");
        _delayUs = delayMs * MicrosPerMs;
        _jitterUs = checked((int)(jitterMs * MicrosPerMs));
        _lossPercent = lossPercent;
        _allowReorder = allowReorder;
        _rng = rng ?? throw new ArgumentNullException(nameof(rng));
    }

    /// <summary>The client-to-server direction of <paramref name="profile"/>.</summary>
    public static LinkConditioner Up(BotLinkProfile profile, IRng rng) =>
        new(profile.UpDelayMs, profile.JitterMs, profile.LossPercent, profile.AllowReorder, rng);

    /// <summary>The server-to-client direction of <paramref name="profile"/>.</summary>
    public static LinkConditioner Down(BotLinkProfile profile, IRng rng) =>
        new(profile.DownDelayMs, profile.JitterMs, profile.LossPercent, profile.AllowReorder, rng);

    public long Admitted { get; private set; }

    public long Dropped { get; private set; }

    /// <summary>
    /// A datagram arrived at <paramref name="nowUs"/>: false when it is lost, else true with the time to deliver it.
    /// Without reordering a datagram is never due before the one admitted before it. The loss check always draws one value
    /// (also at 0 %), and a jitter one more, so the same seed and profile give the same outcome.
    /// </summary>
    public bool Admit(long nowUs, out long dueUs)
    {
        if (_rng.RollPercent(_lossPercent))
        {
            Dropped++;
            dueUs = 0;
            return false;
        }

        long delay = _delayUs + (_jitterUs > 0 ? _rng.NextInt(-_jitterUs, _jitterUs + 1) : 0);
        dueUs = nowUs + Math.Max(0, delay);
        if (!_allowReorder && dueUs < _lastDueUs)
            dueUs = _lastDueUs;
        _lastDueUs = dueUs;
        Admitted++;
        return true;
    }
}
