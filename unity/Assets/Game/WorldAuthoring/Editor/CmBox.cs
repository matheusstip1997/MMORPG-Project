#nullable enable
using UnityEngine;

namespace Game.WorldAuthoring.Editor
{
    /// <summary>
    /// An axis-aligned box in whole cm on the server axes: X = Unity X, Y = Unity Z (ground plane, MovementSim Y),
    /// H = Unity Y (height). Every export decision after this conversion is integer math, so the output is the same on
    /// every machine.
    /// </summary>
    public readonly struct CmBox
    {
        public CmBox(int minX, int minY, int maxX, int maxY, int minH, int maxH)
        {
            MinX = minX;
            MinY = minY;
            MaxX = maxX;
            MaxY = maxY;
            MinH = minH;
            MaxH = maxH;
        }

        public int MinX { get; }
        public int MinY { get; }
        public int MaxX { get; }
        public int MaxY { get; }
        public int MinH { get; }
        public int MaxH { get; }

        /// <summary>Unity meters to whole cm, rounded to nearest.</summary>
        public static int ToCm(float meters) => Mathf.RoundToInt(meters * 100f);

        /// <summary>
        /// The world-space footprint of a local box (center and size, like a BoxCollider). M1 accepts only rotations
        /// about the vertical axis in steps of 90 degrees, so the box stays axis-aligned and exact.
        /// </summary>
        public static bool TryFromLocalBox(Transform transform, Vector3 center, Vector3 size, out CmBox box, out string error)
        {
            box = default;
            if (size.x <= 0f || size.y <= 0f || size.z <= 0f)
            {
                error = $"size {size} must be positive on every axis";
                return false;
            }

            var euler = transform.rotation.eulerAngles;
            if (!IsMultipleOf(euler.x, 360f) || !IsMultipleOf(euler.z, 360f) || !IsMultipleOf(euler.y, 90f))
            {
                error = $"rotation {euler} is not a multiple of 90 degrees around Y only (M1 blockers and regions are axis-aligned)";
                return false;
            }

            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            var half = size * 0.5f;
            for (int i = 0; i < 8; i++)
            {
                var corner = center + new Vector3((i & 1) == 0 ? -half.x : half.x, (i & 2) == 0 ? -half.y : half.y,
                    (i & 4) == 0 ? -half.z : half.z);
                var world = transform.TransformPoint(corner);
                min = Vector3.Min(min, world);
                max = Vector3.Max(max, world);
            }

            const float limitMeters = 80_000f; // inside the Q24.8 range of +-83.8 km (Shared FixedPoint)
            if (Mathf.Abs(min.x) > limitMeters || Mathf.Abs(min.z) > limitMeters || Mathf.Abs(max.x) > limitMeters
                || Mathf.Abs(max.z) > limitMeters || Mathf.Abs(min.y) > limitMeters || Mathf.Abs(max.y) > limitMeters)
            {
                error = "lies outside the simulation coordinate range";
                return false;
            }

            box = new CmBox(ToCm(min.x), ToCm(min.z), ToCm(max.x), ToCm(max.z), ToCm(min.y), ToCm(max.y));
            if (box.MinX >= box.MaxX || box.MinY >= box.MaxY)
            {
                error = "footprint is smaller than 1 cm";
                return false;
            }
            error = string.Empty;
            return true;
        }

        private static bool IsMultipleOf(float degrees, float step)
        {
            const float tolerance = 0.01f;
            float remainder = Mathf.Repeat(degrees, step);
            return remainder < tolerance || step - remainder < tolerance;
        }
    }
}
