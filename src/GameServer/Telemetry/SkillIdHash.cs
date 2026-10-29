namespace GameServer.Telemetry;

/// <summary>
/// Telemetry values are numbers: a skill-tree node or class id is carried as the FNV-1a 32-bit hash of its ASCII id (WP-M2-5),
/// stable across builds, so a report maps it back with the same function over the <c>SkillTree</c> and <c>ClassDef</c> ids.
/// </summary>
public static class SkillIdHash
{
    public static long Of(string id)
    {
        uint hash = 2166136261;
        foreach (char c in id)
        {
            hash ^= (byte)c;
            hash *= 16777619;
        }
        return hash;
    }
}
