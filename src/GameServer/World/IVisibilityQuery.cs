using GameServer.Entities;

namespace GameServer.World;

/// <summary>
/// Whether an observer may see a target (spec 2.4, architecture rule 9: hidden entities are never sent).
/// The interest layer asks it for every candidate inside the interest area.
/// </summary>
public interface IVisibilityQuery
{
    bool CanSee(Entity observer, Entity target);
}

/// <summary>
/// Everything inside the interest area is visible. Correct for M1 until hiding exists.
/// TODO(spec 2.4): VisibilityService with region mode, party, camera shape, Sense vs Detect, line of sight.
/// </summary>
public sealed class EveryoneVisible : IVisibilityQuery
{
    public static readonly EveryoneVisible Instance = new();

    public bool CanSee(Entity observer, Entity target) => true;
}
