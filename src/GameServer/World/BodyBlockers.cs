using GameServer.Entities;
using Shared.Protocol;
using Shared.Simulation;

namespace GameServer.World;

/// <summary>
/// The server's <see cref="IBodyBlockers"/> (R-2.2-07): the living bodies around a mover, from the <see cref="SpatialGrid"/>,
/// that block its kind (<see cref="BodyCollision.Blocks"/>), in entity-id order. Bodies are where the entities stand at that
/// moment of phase 2 or 3 (the entities before the mover in id order have already moved this tick). One per world, on the
/// simulation thread only: <see cref="Bodies"/> is the scratch list every move reuses, so no allocation once warm.
/// </summary>
internal sealed class BodyBlockers : IBodyBlockers
{
    private readonly SpatialGrid _grid;
    private readonly List<Entity> _candidates = new();

    public BodyBlockers(SpatialGrid grid)
    {
        _grid = grid;
    }

    /// <summary>The list one move gathers into and is resolved against.</summary>
    public BodyList Bodies { get; } = new();

    public void Gather(uint moverId, EntityKind moverKind, FixVec2 center, long rangeRaw, BodyList into)
    {
        into.Clear();
        int rangeCm = (int)Math.Min(int.MaxValue / 4, (rangeRaw >> FixedPoint.FractionBits) + 1);
        int x = FixedPoint.ToCmRound(center.X), y = FixedPoint.ToCmRound(center.Y);
        _candidates.Clear();
        _grid.QueryCells(x - rangeCm, y - rangeCm, x + rangeCm, y + rangeCm, _candidates);
        foreach (var entity in _candidates)
        {
            if (entity.Id == moverId || !entity.InGrid || entity.Combatant is not { IsAlive: true } combatant
                || !BodyCollision.Blocks(moverKind, entity.Kind))
                continue;
            var position = entity.Movement.Position;
            long dx = (long)position.X - center.X, dy = (long)position.Y - center.Y;
            if (dx > rangeRaw || dx < -rangeRaw || dy > rangeRaw || dy < -rangeRaw)
                continue;
            into.Add(entity.Id, position, combatant.BodyRadiusRaw);
        }
        _candidates.Clear();
    }

    /// <summary>Gathers into <see cref="Bodies"/> what may block <paramref name="mover"/> moving from where it stands by up to <paramref name="stepRaw"/>.</summary>
    public BodyList For(Entity mover, int moverRadiusRaw, long stepRaw, int maxBodyRadiusRaw)
    {
        Gather(mover.Id, mover.Kind, mover.Movement.Position, stepRaw + moverRadiusRaw + maxBodyRadiusRaw, Bodies);
        return Bodies;
    }
}
