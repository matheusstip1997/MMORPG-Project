using Shared.Protocol;

namespace Shared.Simulation
{
    /// <summary>
    /// Where a mover finds the bodies that may block it (R-2.2-07): the server from its spatial grid, client prediction from
    /// the entity registry (the newest snapshot positions). Implementations add only living bodies that
    /// <see cref="BodyCollision.Blocks"/> the mover's kind, never the mover itself.
    /// </summary>
    public interface IBodyBlockers
    {
        /// <summary>
        /// Clears <paramref name="into"/> and fills it with the blocking bodies whose circle may come within
        /// <paramref name="rangeRaw"/> of <paramref name="center"/> (more is fine: the sweep ignores bodies it cannot reach).
        /// </summary>
        void Gather(uint moverId, EntityKind moverKind, FixVec2 center, long rangeRaw, BodyList into);
    }
}
