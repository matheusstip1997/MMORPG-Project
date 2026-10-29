#nullable enable
using Game.UI.Config;
using Shared.World;
using UnityEngine;

namespace Game.UI.ViewModels.Hud
{
    /// <summary>
    /// The minimap's map image (spec 9.3: "the exported grid rendered by the client, no authored art"; WP-M2-10b): one
    /// texel per <see cref="Shared.Data.Tables.MinimapSettings.CellsPerTexel"/> squared cells, the region's tint where most
    /// of its cells are walkable, the blocked color elsewhere. Row 0 is the south edge (texture rows go up, like world Y),
    /// so the image is north-up. Built once at load (a few ms for the m1 world); no Unity object, the presenter uploads it.
    /// </summary>
    public sealed class MinimapTexture
    {
        private MinimapTexture(int width, int height, int texelCm, int originXCm, int originYCm, Color32[] pixels)
        {
            Width = width;
            Height = height;
            TexelCm = texelCm;
            OriginXCm = originXCm;
            OriginYCm = originYCm;
            Pixels = pixels;
        }

        public int Width { get; }
        public int Height { get; }

        /// <summary>World centimeters per texel side.</summary>
        public int TexelCm { get; }

        /// <summary>World position of the texture's south-west corner.</summary>
        public int OriginXCm { get; }
        public int OriginYCm { get; }

        /// <summary>Row-major from the south row up (<c>Texture2D.SetPixels32</c> order).</summary>
        public Color32[] Pixels { get; }

        public Color32 PixelAt(int xCm, int yCm)
        {
            int x = Mathf.Clamp((xCm - OriginXCm) / TexelCm, 0, Width - 1);
            int y = Mathf.Clamp((yCm - OriginYCm) / TexelCm, 0, Height - 1);
            return Pixels[y * Width + x];
        }

        public static MinimapTexture From(WorldGrid grid, WorldIndex index, MinimapConfig config)
        {
            int k = config.Settings.CellsPerTexel;
            int width = (grid.Width + k - 1) / k, height = (grid.Height + k - 1) / k;
            // Ground color per region index (0 = no region).
            var ground = new Color32[grid.MaxRegionIndex + 1];
            ground[0] = config.WalkableColor;
            foreach (var region in index.Regions)
            {
                if (region.Index > 0 && region.Index < ground.Length)
                    ground[region.Index] = config.GroundColor(region.Id);
            }
            Color32 blocked = config.BlockedColor;

            var pixels = new Color32[width * height];
            for (int ty = 0; ty < height; ty++)
            {
                for (int tx = 0; tx < width; tx++)
                {
                    int walkable = 0, cells = 0, region = 0;
                    for (int dy = 0; dy < k; dy++)
                    {
                        for (int dx = 0; dx < k; dx++)
                        {
                            int column = tx * k + dx, row = ty * k + dy;
                            if (!grid.InBounds(column, row))
                                continue;
                            cells++;
                            if (!grid.IsWalkableCell(column, row))
                                continue;
                            walkable++;
                            if (region == 0)
                                region = grid.RegionIndexOfCell(column, row);
                        }
                    }
                    pixels[ty * width + tx] = walkable * 2 > cells ? ground[region < ground.Length ? region : 0] : blocked;
                }
            }
            return new MinimapTexture(width, height, k * grid.CellSizeCm, grid.OriginXCm, grid.OriginYCm, pixels);
        }
    }
}
