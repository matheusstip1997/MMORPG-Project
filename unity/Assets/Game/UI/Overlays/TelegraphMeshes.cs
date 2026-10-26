#nullable enable
using System.Collections.Generic;
using Game.UI.Config;
using Shared.Data.Tables;
using UnityEngine;

namespace Game.UI.Overlays
{
    /// <summary>
    /// Flat ground meshes for telegraph shapes (spec 9.3 <c>telegraph</c>), in the attacker's frame: metres, +z forward
    /// along the facing, +x to its right, y 0 (the same frame as <c>WorldUnits.FacingRotation</c>). The geometry follows
    /// <c>HitShape</c>: a cone has its apex at the origin, a box runs from the origin forward and is centered across, a
    /// capsule is a radius around a forward segment (length 0: a circle). Built once per action and cached.
    /// </summary>
    public static class TelegraphMeshes
    {
        /// <summary>Segments of a full circle; arcs use their share of it.</summary>
        public const int CircleSegments = 40;

        private const float CmPerMetre = 100f;

        public static Mesh Build(TelegraphShape[] shapes)
        {
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            foreach (var shape in shapes)
                Add(shape, vertices, triangles);
            var mesh = new Mesh { name = "Telegraph" };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static void Add(TelegraphShape shape, List<Vector3> vertices, List<int> triangles)
        {
            var origin = new Vector3(shape.SideCm / CmPerMetre, 0f, shape.ForwardCm / CmPerMetre);
            switch (shape.Kind)
            {
                case HitShapeKind.Cone:
                    Fan(origin, shape.ReachCm / CmPerMetre, -shape.HalfAngleDeg, shape.HalfAngleDeg, vertices, triangles);
                    break;
                case HitShapeKind.Box:
                    float half = shape.BreadthCm / CmPerMetre;
                    float length = shape.ReachCm / CmPerMetre;
                    Quad(origin + new Vector3(-half, 0f, 0f), origin + new Vector3(-half, 0f, length),
                        origin + new Vector3(half, 0f, length), origin + new Vector3(half, 0f, 0f), vertices, triangles);
                    break;
                default:
                    float radius = shape.BreadthCm / CmPerMetre;
                    float segment = shape.ReachCm / CmPerMetre;
                    // Back half circle around the origin, front half circle around the segment's end, and the rectangle between.
                    Fan(origin, radius, 90f, 270f, vertices, triangles);
                    var tip = origin + new Vector3(0f, 0f, segment);
                    Fan(tip, radius, -90f, 90f, vertices, triangles);
                    if (segment > 0f)
                        Quad(origin + new Vector3(-radius, 0f, 0f), tip + new Vector3(-radius, 0f, 0f),
                            tip + new Vector3(radius, 0f, 0f), origin + new Vector3(radius, 0f, 0f), vertices, triangles);
                    break;
            }
        }

        /// <summary>A sector around <paramref name="center"/>; angles in degrees clockwise from forward (+z), like facings.</summary>
        private static void Fan(Vector3 center, float radius, float fromDeg, float toDeg, List<Vector3> vertices, List<int> triangles)
        {
            int steps = Mathf.Max(2, Mathf.CeilToInt(CircleSegments * (toDeg - fromDeg) / 360f));
            int centerIndex = vertices.Count;
            vertices.Add(center);
            for (int i = 0; i <= steps; i++)
            {
                float angle = Mathf.Lerp(fromDeg, toDeg, i / (float)steps) * Mathf.Deg2Rad;
                vertices.Add(center + new Vector3(Mathf.Sin(angle) * radius, 0f, Mathf.Cos(angle) * radius));
            }
            for (int i = 0; i < steps; i++)
            {
                // Clockwise seen from above (+y) faces up in Unity.
                triangles.Add(centerIndex);
                triangles.Add(centerIndex + 1 + i);
                triangles.Add(centerIndex + 2 + i);
            }
        }

        /// <summary>A quad given clockwise from above: back left, front left, front right, back right.</summary>
        private static void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, List<Vector3> vertices, List<int> triangles)
        {
            int start = vertices.Count;
            vertices.Add(a);
            vertices.Add(b);
            vertices.Add(c);
            vertices.Add(d);
            triangles.Add(start);
            triangles.Add(start + 1);
            triangles.Add(start + 2);
            triangles.Add(start);
            triangles.Add(start + 2);
            triangles.Add(start + 3);
        }
    }
}
