namespace GameServer.Net.Security;

/// <summary>
/// Integer token bucket (R-2.5-03): holds up to <c>capacity</c> tokens and refills <c>tokensPerPeriod</c> every
/// <c>periodMs</c>, continuously. Tokens are stored scaled by the period so refills stay exact without floating point.
/// Time comes from the caller (an <c>IClock</c>); a clock that steps back refills nothing.
/// A new bucket starts full.
/// </summary>
public struct TokenBucket
{
    private long _scaledTokens;
    private long _lastRefillMs;
    private bool _started;

    public bool TryTake(long nowMs, int capacity, int tokensPerPeriod, int periodMs)
    {
        Refill(nowMs, capacity, tokensPerPeriod, periodMs);
        if (_scaledTokens < periodMs)
            return false;
        _scaledTokens -= periodMs;
        return true;
    }

    /// <summary>True when the bucket has refilled to capacity, so forgetting it changes nothing.</summary>
    public bool IsFull(long nowMs, int capacity, int tokensPerPeriod, int periodMs)
    {
        Refill(nowMs, capacity, tokensPerPeriod, periodMs);
        return _scaledTokens >= (long)capacity * periodMs;
    }

    private void Refill(long nowMs, int capacity, int tokensPerPeriod, int periodMs)
    {
        var max = (long)capacity * periodMs;
        if (!_started)
        {
            _started = true;
            _scaledTokens = max;
            _lastRefillMs = nowMs;
            return;
        }
        if (nowMs <= _lastRefillMs)
            return;

        var elapsed = nowMs - _lastRefillMs;
        _lastRefillMs = nowMs;
        // Past this point the bucket is full anyway; the bound also keeps the product below long.MaxValue.
        if (elapsed >= max)
        {
            _scaledTokens = max;
            return;
        }
        _scaledTokens = Math.Min(max, _scaledTokens + elapsed * tokensPerPeriod);
    }
}
