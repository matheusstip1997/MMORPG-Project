using Shared.Protocol;

namespace GameServer.World.Nodes;

/// <summary>Where a harvest stands between its request and its effect point (spec 5.2, WP-M3-5).</summary>
internal enum PendingHarvestState : byte
{
    /// <summary>Checked in phase 1; the harvest action starts (or is refused) in phase 3.</summary>
    Requested = 0,
    /// <summary>The harvest action runs (the channel); nothing is harvested until its harvest effect fires.</summary>
    Running = 1,
}

/// <summary>A harvest asked for and not yet at its effect point: the node it names and the node type's runtime entry.</summary>
internal sealed class PendingHarvest
{
    public PendingHarvest(uint requestId, string opKey, uint nodeEntityId, HarvestNodeEntry entry, int treeYieldPercent,
        int gemFindPoints)
    {
        TreeYieldPercent = treeYieldPercent;
        GemFindPoints = gemFindPoints;
        RequestId = requestId;
        OpKey = opKey;
        NodeEntityId = nodeEntityId;
        Entry = entry;
    }

    public uint RequestId { get; }
    public string OpKey { get; }
    public uint NodeEntityId { get; }
    public HarvestNodeEntry Entry { get; }
    public PendingHarvestState State { get; set; }

    /// <summary>The job tree's yield bonus read when the harvest was accepted (it keeps what it read, decision 10).</summary>
    public int TreeYieldPercent { get; }

    /// <summary>The job tree's gem-find bonus in percentage points, read when the harvest was accepted.</summary>
    public int GemFindPoints { get; }

    /// <summary>The harvester lost HP while the channel ran (spec 5.2 "breaks on damage"): the next phase 3 breaks it.</summary>
    public bool Damaged { get; set; }
}

/// <summary>A harvest waiting for the save that holds its items (rule 5: confirm only after the commit).</summary>
internal readonly record struct PendingHarvestConfirmation(string OpKey, HarvestResult Result);

/// <summary>
/// One player's harvest traffic (spec 5.2, WP-M3-5): the harvest in progress (at most one), refusals and interruptions for
/// phase 10, harvests waiting for their commit, and the answers of applied operation keys (a repeated request gets the
/// first answer). World thread only.
/// </summary>
internal sealed class HarvestAnswers
{
    private readonly Dictionary<string, HarvestResult> _answers = new(StringComparer.Ordinal);

    /// <summary>The harvest between its request and its effect point; null when none.</summary>
    public PendingHarvest? Current { get; set; }

    /// <summary>Refusals and interruptions waiting for phase 10, oldest first.</summary>
    public List<HarvestResult> Refusals { get; } = new();

    /// <summary>Harvests waiting for their save, oldest first.</summary>
    public List<PendingHarvestConfirmation> Confirmations { get; } = new();

    /// <summary>The answer of an applied operation key, or null.</summary>
    public HarvestResult? Answer(string opKey) => _answers.TryGetValue(opKey, out var answer) ? answer : null;

    public void RecordAnswer(string opKey, HarvestResult answer) => _answers[opKey] = answer;

    /// <summary>
    /// A new connection or none (bind, unbind): answers nobody will read are dropped, and a harvest not yet at its effect
    /// point harvests nothing (its action may run on; the effect then finds no harvest). An applied harvest still commits
    /// and is announced (<c>Harvested</c>) by the service.
    /// </summary>
    public void Reset()
    {
        Current = null;
        Refusals.Clear();
        Confirmations.Clear();
        _answers.Clear(); // operation keys carry the connection, so the old ones never repeat
    }

    /// <summary>A disconnected player: answers nobody will read.</summary>
    public void ClearAnswers()
    {
        Refusals.Clear();
        Confirmations.Clear();
    }
}
