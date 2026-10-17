namespace GameServer.Net.Security;

/// <summary>
/// Sliding replay window over packet sequence numbers (R-2.5-02), as in DTLS and IPsec: a sequence number is
/// accepted once, and only if it is newer than the highest seen minus <see cref="Size"/>. Sequence 0 is never valid.
/// Call <see cref="CanAccept"/> before authenticating and <see cref="Mark"/> only after the packet authenticated,
/// so a forged packet cannot burn a sequence number.
/// </summary>
public struct ReplayWindow
{
    /// <summary>Width of the bitmap: a protocol constant, not a tuning value.</summary>
    public const int Size = 64;

    private ulong _highest;
    private ulong _seen;

    public readonly ulong Highest => _highest;

    public readonly bool CanAccept(ulong sequence)
    {
        if (sequence == 0)
            return false;
        if (sequence > _highest)
            return true;
        var age = _highest - sequence;
        return age < Size && (_seen & (1UL << (int)age)) == 0;
    }

    public void Mark(ulong sequence)
    {
        if (sequence > _highest)
        {
            var shift = sequence - _highest;
            _seen = shift >= Size ? 0 : _seen << (int)shift;
            _seen |= 1;
            _highest = sequence;
        }
        else if (_highest - sequence < Size)
        {
            // C# masks shift counts to 6 bits, so the bound above is what keeps this correct.
            _seen |= 1UL << (int)(_highest - sequence);
        }
    }
}
