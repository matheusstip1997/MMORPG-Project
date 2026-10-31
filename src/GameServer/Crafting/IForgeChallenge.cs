using Shared.Simulation.Crafting;

namespace GameServer.Crafting;

/// <summary>
/// The forge minigame of a job (spec 5.3 <c>IForgeChallenge</c>, R-5.3-02): the server generates its parameters and seed when
/// the session runs, the client plays and sends timestamped inputs, and the server scores them on its own timeline within a
/// latency tolerance. Its contribution is capped by the <c>minigame</c> row of <c>QualityCaps</c>. Each job may plug in its
/// own; WP-M3-10 adds the first (the Blacksmith's timing bar). World thread only.
/// </summary>
public interface IForgeChallenge
{
    /// <summary>The highest score a session of <paramref name="definition"/> can reach (the preview's top).</summary>
    int MaxScore(ForgeDefinition definition);

    /// <summary>The session's score at Finish; a missing or impossible input scores the minimum, never fails the craft.</summary>
    int Score(ForgeSession session);
}

/// <summary>No minigame (M3 until WP-M3-10): every session scores 0.</summary>
public sealed class NoForgeChallenge : IForgeChallenge
{
    public static readonly NoForgeChallenge Instance = new();

    public int MaxScore(ForgeDefinition definition) => 0;

    public int Score(ForgeSession session) => 0;
}
