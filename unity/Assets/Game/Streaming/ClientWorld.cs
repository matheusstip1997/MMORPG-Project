#nullable enable
using System.IO;
using Shared.World;
using UnityEngine;

namespace Game.Gameplay.Streaming
{
    /// <summary>
    /// Loads the exported world (spec 11.6) the server simulates, for client prediction: the same files, verified with
    /// the shared <see cref="WorldPackage"/> checks the server's loader runs (manifest format and version, every file's
    /// size and SHA-256, grid geometry). In the editor the files come straight from the repository's
    /// <c>data/world/&lt;id&gt;</c>.
    /// TODO(build): copy data/world into StreamingAssets for player builds, like the config tables (ClientConfig);
    /// until then a build looks for <c>StreamingAssets/world/&lt;id&gt;</c> and fails with a clear error.
    /// </summary>
    public static class ClientWorld
    {
        public static string Directory(string worldId) =>
#if UNITY_EDITOR
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "data", "world", worldId));
#else
            Path.Combine(Application.streamingAssetsPath, "world", worldId);
#endif

        public static bool TryLoadGrid(string worldId, out WorldGrid? grid, out string error) =>
            TryLoadGridFrom(Directory(worldId), out grid, out error);

        public static bool TryLoadGridFrom(string directory, out WorldGrid? grid, out string error)
        {
            if (WorldPackage.TryLoadGrid(name => ReadFile(directory, name), out grid, out var reason))
            {
                error = string.Empty;
                return true;
            }
            error = $"World in {directory} is invalid (re-export it, then run src/Tools/DataValidator): {reason}";
            return false;
        }

        /// <summary>The world's regions and markers (the minimap, WP-M2-10b), after the same file checks as the grid.</summary>
        public static bool TryLoadIndex(string worldId, out WorldIndex? index, out string error)
        {
            var directory = Directory(worldId);
            if (WorldPackage.TryLoadIndex(name => ReadFile(directory, name), out index, out var reason))
            {
                error = string.Empty;
                return true;
            }
            error = $"World in {directory} is invalid (re-export it, then run src/Tools/DataValidator): {reason}";
            return false;
        }

        private static byte[]? ReadFile(string directory, string name)
        {
            var path = Path.Combine(directory, name);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
    }
}
