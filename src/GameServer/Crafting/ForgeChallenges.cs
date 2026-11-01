using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Crafting;

namespace GameServer.Crafting;

/// <summary>
/// The forge minigame per job (spec 5.3 "each job can plug in its own challenge", M3 decision 12; WP-M3-10): each
/// <see cref="ForgeChallengeDef"/> row gives its job a challenge (<see cref="TimingBarChallenge"/>, or
/// <see cref="NoForgeChallenge"/> for kind none), and a session plays the one of its definition's job. A job without a row
/// plays none. Set on <c>GameWorld.ForgeChallenge</c>.
/// </summary>
public sealed class ForgeChallenges : IForgeChallenge
{
    private readonly Dictionary<string, IForgeChallenge> _byJob = new(StringComparer.Ordinal);

    public ForgeChallenges(ForgeRules rules)
    {
        foreach (var (jobId, row) in rules.Challenges)
        {
            _byJob[jobId] = row.Kind switch
            {
                ForgeChallengeKind.TimingBar => new TimingBarChallenge(row, rules),
                _ => NoForgeChallenge.Instance,
            };
        }
    }

    /// <summary>The challenges of <paramref name="rules"/>; null when no job has a row (the forge then plays none).</summary>
    public static ForgeChallenges? From(ForgeRules rules) => rules.Challenges.Count == 0 ? null : new ForgeChallenges(rules);

    public IForgeChallenge Of(string jobId) => _byJob.TryGetValue(jobId, out var challenge) ? challenge : NoForgeChallenge.Instance;

    public int MaxScore(ForgeDefinition definition) => Of(definition.JobId).MaxScore(definition);

    public int Score(ForgeSession session) => Of(session).Score(session);

    public void Begin(ForgeSession session) => Of(session).Begin(session);

    public ForgeHit OnAction(ForgeSession session, int actionNetId, long nowMs) => Of(session).OnAction(session, actionNetId, nowMs);

    public ForgeHit Input(ForgeSession session, int round, long offsetMs, int roundTripMs, long nowMs) =>
        Of(session).Input(session, round, offsetMs, roundTripMs, nowMs);

    public ForgeHit Close(ForgeSession session) => Of(session).Close(session);

    public void Range(ForgeSession session, out int min, out int max) => Of(session).Range(session, out min, out max);

    public void Describe(ForgeSession session, ForgeState state) => Of(session).Describe(session, state);

    private IForgeChallenge Of(ForgeSession session) => Of(session.Definition.JobId);
}
