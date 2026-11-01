using Shared.Protocol;
using Shared.Simulation.Crafting;

namespace GameServer.Crafting;

/// <summary>A round of a forge challenge that closed: its number (from 1), its score, the press's lag and how it was judged.</summary>
public readonly record struct ForgeHit(int Round, int Score, long LagMs, ForgeHitOutcome Outcome)
{
    /// <summary>Nothing closed.</summary>
    public static readonly ForgeHit None = new(0, 0, 0, ForgeHitOutcome.None);

    public bool Closed => Outcome != ForgeHitOutcome.None;
}

/// <summary>
/// The forge minigame of a job (spec 5.3 <c>IForgeChallenge</c>, R-5.3-02, M3 decision 12): the server generates its parameters
/// and seed when the session runs, the client plays and sends timestamped inputs, and the server scores them on its own
/// timeline within a latency tolerance. Its contribution is capped by the <c>minigame</c> row of <c>QualityCaps</c>. Each job
/// plugs in its own (<see cref="ForgeChallenges"/>: the Blacksmith's timing bar, WP-M3-10; none for the Jeweler). It keeps its
/// state on the session (<see cref="ForgeSession.Challenge"/>), never touches the inputs, the reservations or the save, and a
/// missing, late or impossible input scores the minimum without failing the craft. World thread only; only
/// <see cref="ForgeService"/> calls it.
/// </summary>
public interface IForgeChallenge
{
    /// <summary>The highest score a session of <paramref name="definition"/> can reach.</summary>
    int MaxScore(ForgeDefinition definition);

    /// <summary>The session's score at Finish: its closed rounds, capped by <see cref="MaxScore"/> (an open round adds 0).</summary>
    int Score(ForgeSession session);

    /// <summary>The session started: prepares its state when its job plays a challenge with an action it allows.</summary>
    void Begin(ForgeSession session);

    /// <summary>
    /// The session accepted forge action <paramref name="actionNetId"/>: an open round closes unplayed (returned), and the
    /// challenge's action opens the next round, issued at <paramref name="nowMs"/>.
    /// </summary>
    ForgeHit OnAction(ForgeSession session, int actionNetId, long nowMs);

    /// <summary>A press on round <paramref name="round"/>, <paramref name="offsetMs"/> into it on the client's clock.</summary>
    ForgeHit Input(ForgeSession session, int round, long offsetMs, int roundTripMs, long nowMs);

    /// <summary>The session finishes: an open round closes unplayed (returned).</summary>
    ForgeHit Close(ForgeSession session);

    /// <summary>The minigame part of the preview: the closed rounds' score, and that plus what the open round can add.</summary>
    void Range(ForgeSession session, out int min, out int max);

    /// <summary>Writes the challenge's part of the window's state (the open round, the scores so far).</summary>
    void Describe(ForgeSession session, ForgeState state);
}

/// <summary>No minigame (the Jeweler in M3; worlds without <c>ForgeChallengeDef</c>): every session scores 0.</summary>
public sealed class NoForgeChallenge : IForgeChallenge
{
    public static readonly NoForgeChallenge Instance = new();

    public int MaxScore(ForgeDefinition definition) => 0;

    public int Score(ForgeSession session) => 0;

    public void Begin(ForgeSession session)
    {
    }

    public ForgeHit OnAction(ForgeSession session, int actionNetId, long nowMs) => ForgeHit.None;

    public ForgeHit Input(ForgeSession session, int round, long offsetMs, int roundTripMs, long nowMs) =>
        new(round, 0, 0, ForgeHitOutcome.Impossible);

    public ForgeHit Close(ForgeSession session) => ForgeHit.None;

    public void Range(ForgeSession session, out int min, out int max) => min = max = 0;

    public void Describe(ForgeSession session, ForgeState state)
    {
    }
}
