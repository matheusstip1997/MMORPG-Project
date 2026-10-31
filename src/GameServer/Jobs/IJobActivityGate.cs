namespace GameServer.Jobs;

/// <summary>
/// A running job activity session of a character (a harvest channel, WP-M3-5; a forge session, WP-M3-6). A job tree respec
/// is refused while any registered gate says so (<c>JobActivityRunning</c>; <c>docs/design/job-trees.md</c> decision 6), so
/// a session keeps the bonuses it read at its start (<see cref="IJobBonusQuery"/>). World thread only.
/// </summary>
public interface IJobActivityGate
{
    bool InSession(long characterId);
}

/// <summary>The gates the world asks before a respec (WP-M3-7). Empty until the harvest and forge services register theirs.</summary>
public sealed class JobActivityGates
{
    private readonly List<IJobActivityGate> _gates = new();

    public int Count => _gates.Count;

    public void Add(IJobActivityGate gate) => _gates.Add(gate);

    public bool InSession(long characterId)
    {
        foreach (var gate in _gates)
        {
            if (gate.InSession(characterId))
                return true;
        }
        return false;
    }
}
