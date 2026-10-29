#nullable enable
using Shared.Protocol;
using Shared.Simulation;
using Shared.Simulation.Actions;

namespace Game.Gameplay.Entities
{
    /// <summary>
    /// Client prediction's <see cref="IBodyBlockers"/> (R-2.2-07): the living entities of the <see cref="EntityRegistry"/> that
    /// block the mover's kind (for the local player: monsters), at their <b>newest snapshot</b> position, not the interpolated
    /// one drawn <c>InterpolationDelayMs</c> behind: the newest is the nearest to where the server collides with them.
    /// Alive means what the target lock reads (<c>TargetCandidates.IsLockable</c>): not in the dead state, HP above 0, not
    /// leaving interest. Plain C# with no UnityEngine API; allocation-free.
    /// </summary>
    public sealed class RegistryBodyBlockers : IBodyBlockers
    {
        private readonly EntityRegistry _registry;
        private readonly BodyRadii _radii;

        public RegistryBodyBlockers(EntityRegistry registry, BodyRadii radii)
        {
            _registry = registry;
            _radii = radii;
        }

        public void Gather(uint moverId, EntityKind moverKind, FixVec2 center, long rangeRaw, BodyList into)
        {
            into.Clear();
            foreach (var entity in _registry.EntityValues)
            {
                if (entity.Id == moverId || !BodyCollision.Blocks(moverKind, entity.Kind))
                    continue;
                if (entity.ActionState == ActionState.Dead || entity.HpBucket == 0 || entity.LeaveTick.HasValue)
                    continue;
                if (!_radii.TryGetRaw(entity.Kind, entity.DefId, out int radiusRaw) || !entity.Samples.TryNewest(out var sample))
                    continue;
                var position = FixVec2.FromCm(sample.XCm, sample.ZCm); // snapshot axes: Y is the height
                long dx = (long)position.X - center.X, dy = (long)position.Y - center.Y;
                if (dx > rangeRaw || dx < -rangeRaw || dy > rangeRaw || dy < -rangeRaw)
                    continue;
                into.Add(entity.Id, position, radiusRaw);
            }
        }
    }
}
