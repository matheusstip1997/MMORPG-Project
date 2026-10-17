using Shared.Data.Tables;

namespace GameServer.Sessions;

/// <summary>
/// Disconnected characters that stay in the world for a while (R-2.1-08), with their expiry as UTC ms (architecture
/// rule 6: timestamps, not timers). World thread only. Expired characters come out in character-id order.
/// </summary>
public sealed class LingerManager
{
    private readonly StartingValuesM1 _values;
    private readonly List<(long CharacterId, long ExpiresAtMs)> _lingering = new(); // sorted by character id

    public LingerManager(StartingValuesM1 values) => _values = values;

    public int Count => _lingering.Count;

    /// <summary>
    /// How long a character that disconnects now lingers (R-2.1-08): <c>LingerInCombatMs</c> while it is in combat
    /// (<c>lastCombatAt + CombatStateMs</c> is in the future, R-2.1-07), <c>LingerOutOfCombatMs</c> otherwise.
    /// TODO(C2-regions): no linger in safe zones; look up the region flags of the position with C2's
    /// <c>WorldMap.FlagsAt</c> / <c>RegionAt</c> once they exist. TODO(M2, law): a dead bad-honor character lingers at
    /// least the arrest window.
    /// </summary>
    public static long WindowMs(StartingValuesM1 values, long lastCombatAtMs, long nowMs)
    {
        bool inCombat = lastCombatAtMs > 0 && nowMs < lastCombatAtMs + values.CombatStateMs;
        return inCombat ? values.LingerInCombatMs : values.LingerOutOfCombatMs;
    }

    /// <summary>Starts the linger and returns its window; a zero window is not tracked (the caller removes at once).</summary>
    public long Start(long characterId, long lastCombatAtMs, long nowMs)
    {
        var window = WindowMs(_values, lastCombatAtMs, nowMs);
        Cancel(characterId);
        if (window <= 0)
            return 0;
        var index = _lingering.Count;
        while (index > 0 && _lingering[index - 1].CharacterId > characterId)
            index--;
        _lingering.Insert(index, (characterId, nowMs + window));
        return window;
    }

    public bool IsLingering(long characterId) => IndexOf(characterId) >= 0;

    /// <summary>A reconnect within the window (R-2.1-08) or a removal for another reason.</summary>
    public bool Cancel(long characterId)
    {
        var index = IndexOf(characterId);
        if (index < 0)
            return false;
        _lingering.RemoveAt(index);
        return true;
    }

    /// <summary>Removes every linger that ended at or before <paramref name="nowMs"/> and appends its character id.</summary>
    public void TakeExpired(long nowMs, List<long> expired)
    {
        for (int i = 0; i < _lingering.Count;)
        {
            if (_lingering[i].ExpiresAtMs <= nowMs)
            {
                expired.Add(_lingering[i].CharacterId);
                _lingering.RemoveAt(i);
            }
            else
                i++;
        }
    }

    private int IndexOf(long characterId)
    {
        for (int i = 0; i < _lingering.Count; i++)
        {
            if (_lingering[i].CharacterId == characterId)
                return i;
        }
        return -1;
    }
}
