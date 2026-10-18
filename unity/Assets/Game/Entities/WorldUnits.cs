#nullable enable
using UnityEngine;

namespace Game.Gameplay.Entities
{
    /// <summary>
    /// The one place that converts between game coordinates and Unity world space.
    /// <list type="bullet">
    /// <item>Simulation (<c>MovementSim</c>): ground-plane X and Y in cm, +Y = W = forward at camera yaw 0, plus a height.</item>
    /// <item>Snapshots: Y-up cm, ground Y carried as Z (see <see cref="SnapshotAxes"/>).</item>
    /// <item>Unity: Y-up metres. Ground X = Unity x, ground Y = Unity z, height = Unity y.</item>
    /// <item>Facing: binary angle, 0 = +Y, clockwise seen from above. Unity yaw is clockwise from +z seen from above, so
    /// yaw degrees = facing / 65536 * 360.</item>
    /// </list>
    /// </summary>
    public static class WorldUnits
    {
        /// <summary>One Unity unit is one metre.</summary>
        public const float CmPerUnit = 100f;

        public const float BinaryAnglePerTurn = 65536f;

        public static Vector3 FromGroundCm(float groundXCm, float groundYCm, float heightCm) =>
            new Vector3(groundXCm / CmPerUnit, heightCm / CmPerUnit, groundYCm / CmPerUnit);

        /// <summary>Snapshot axes are already Y-up: only the scale changes.</summary>
        public static Vector3 FromSnapshotCm(float xCm, float yCm, float zCm) =>
            new Vector3(xCm / CmPerUnit, yCm / CmPerUnit, zCm / CmPerUnit);

        public static float FacingToYawDegrees(float facing) => facing / BinaryAnglePerTurn * 360f;

        public static Quaternion FacingRotation(float facing) => Quaternion.Euler(0f, FacingToYawDegrees(facing), 0f);

        /// <summary>
        /// Binary angle of a horizontal Unity direction (y is ignored). False for a direction with no horizontal length.
        /// Input side only: the result is quantized before it reaches the simulation.
        /// </summary>
        public static bool TryFacingFromDirection(Vector3 direction, out ushort facing)
        {
            facing = 0;
            if (direction.x * direction.x + direction.z * direction.z < 1e-8f)
                return false;
            float degrees = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg; // clockwise from +z
            facing = (ushort)Mathf.RoundToInt(Mathf.Repeat(degrees, 360f) / 360f * BinaryAnglePerTurn);
            return true;
        }
    }
}
