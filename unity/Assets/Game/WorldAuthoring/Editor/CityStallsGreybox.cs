#nullable enable
using Shared.World;
using UnityEngine;

namespace Game.WorldAuthoring.Editor
{
    /// <summary>
    /// The stall places of the M1 city (WP-M4-8, spec 4.5, M4 decision 7: stall places are <c>stall</c> markers in the city, the
    /// cap per city is their count, 12 placeholders). Two rows of six along the south side of the square (Z -21 facing south and
    /// Z -28 facing north, an aisle between), X -46 to -12: south of the shop (Z -17..-7) and the auction house (Z -8..0), at least
    /// 6 m apart (twice <c>StallSettings.interactRangeCm</c>, so one place is in reach at a time) and more than 6 m from every NPC.
    /// Called by <see cref="SampleWorldBuilder.Build"/>; the places are placeholders for the market district of the city plans
    /// (<c>docs/design/city-layouts.md</c>).
    /// </summary>
    public static class CityStallsGreybox
    {
        public const string StallPrefix = "m1.stall.";

        private static readonly float[] RowX = { -46f, -39f, -32f, -25f, -18f, -12f };

        public static void Build(Transform city)
        {
            var stalls = new GameObject("Stall Places").transform;
            stalls.SetParent(city, false);
            int n = 1;
            foreach (var (z, yaw) in new[] { (-21f, 180f), (-28f, 0f) })
            {
                foreach (float x in RowX)
                {
                    var marker = new GameObject($"Stall Place {n:00}").AddComponent<ServerMarker>();
                    marker.transform.SetParent(stalls, false);
                    marker.markerId = $"{StallPrefix}{n:00}";
                    marker.kind = MarkerKind.Stall;
                    marker.transform.SetPositionAndRotation(new Vector3(x, 0f, z), Quaternion.Euler(0f, yaw, 0f));
                    n++;
                }
            }
        }
    }
}
