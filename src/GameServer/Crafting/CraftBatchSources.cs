using GameServer.Persistence;

namespace GameServer.Crafting;

/// <summary>
/// The <c>craft_ops</c> producers of a world as one <see cref="ICraftBatchSource"/> for the <see cref="SaveScheduler"/>
/// (WP-M3-8): the forge, enchanting, and the gem services that come later. A save takes each source's pending operations in
/// a fixed order (the order given here) into one <see cref="CraftBatch"/>, and tells each of them when it committed.
/// </summary>
public sealed class CraftBatchSources : ICraftBatchSource
{
    private readonly ICraftBatchSource[] _sources;

    public CraftBatchSources(params ICraftBatchSource?[] sources)
    {
        _sources = sources.Where(s => s != null).Select(s => s!).ToArray();
    }

    public int Count => _sources.Length;

    /// <summary>One source as itself, several as this composite, none as null.</summary>
    public ICraftBatchSource? AsSource() => _sources.Length switch
    {
        0 => null,
        1 => _sources[0],
        _ => this,
    };

    public CraftBatch? TakeBatch(long characterId, Guid saveId)
    {
        List<CraftOpRecord>? ops = null;
        foreach (var source in _sources)
        {
            if (source.TakeBatch(characterId, saveId) is not { } batch)
                continue;
            ops ??= new List<CraftOpRecord>();
            ops.AddRange(batch.Ops);
        }
        return ops == null ? null : new CraftBatch(ops);
    }

    public void Committed(long characterId, Guid saveId)
    {
        foreach (var source in _sources)
            source.Committed(characterId, saveId);
    }
}
