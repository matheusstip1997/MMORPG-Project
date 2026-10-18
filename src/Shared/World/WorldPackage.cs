using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Shared.Data;

namespace Shared.World
{
    /// <summary>
    /// The checks that make an exported world directory trustworthy (spec 11.6), shared by the server's
    /// <c>WorldLoader</c> and the Unity client so both accept exactly the same files: the manifest's file list, the
    /// size and SHA-256 of every file, and the grid's geometry against the manifest. <see cref="TryLoadGrid"/> is the
    /// client's whole path (it only needs the grid for prediction); the server runs the same checks inside its loader
    /// and goes on to regions, markers and blockers. Load time only; it allocates freely.
    /// </summary>
    public static class WorldPackage
    {
        /// <summary>Lowercase hex SHA-256, as the manifest stores it.</summary>
        public static string Sha256Hex(byte[] data)
        {
            byte[] hash;
            using (var sha = SHA256.Create())
                hash = sha.ComputeHash(data);
            var text = new StringBuilder(hash.Length * 2);
            foreach (byte b in hash)
                text.Append(b.ToString("x2"));
            return text.ToString();
        }

        /// <summary>Null when the manifest lists exactly <see cref="WorldFiles.DataFiles"/> in order; otherwise why not.</summary>
        public static string? CheckFileList(IReadOnlyList<string> listed)
        {
            bool same = listed.Count == WorldFiles.DataFiles.Length;
            for (int i = 0; same && i < listed.Count; i++)
                same = string.Equals(listed[i], WorldFiles.DataFiles[i], StringComparison.Ordinal);
            return same ? null : $"lists [{string.Join(", ", listed)}], expected [{string.Join(", ", WorldFiles.DataFiles)}]";
        }

        /// <summary>Null when a file has the size and SHA-256 the manifest lists for it; otherwise why not.</summary>
        public static string? CheckFile(byte[] bytes, long expectedBytes, string expectedSha256)
        {
            if (bytes.LongLength != expectedBytes)
                return $"is {bytes.LongLength} bytes, the manifest says {expectedBytes}";
            if (!string.Equals(Sha256Hex(bytes), expectedSha256, StringComparison.Ordinal))
                return "SHA-256 does not match the manifest (edited by hand, or a partial export)";
            return null;
        }

        /// <summary>True when the decoded grid has the manifest's cell size, size in cells and bounds ([min, max) cm).</summary>
        public static bool GeometryMatches(WorldGrid grid, int cellSizeCm, int widthCells, int heightCells,
            int minXCm, int minYCm, int maxXCm, int maxYCm) =>
            grid.CellSizeCm == cellSizeCm && grid.Width == widthCells && grid.Height == heightCells
            && grid.OriginXCm == minXCm && grid.OriginYCm == minYCm
            && (long)grid.OriginXCm + (long)grid.Width * grid.CellSizeCm == maxXCm
            && (long)grid.OriginYCm + (long)grid.Height * grid.CellSizeCm == maxYCm;

        /// <summary>
        /// Reads <c>manifest.json</c> with the portable JSON parser, checks its format and version, the file list and
        /// every file's size and hash, decodes <c>grid.bin</c> and checks it against the manifest.
        /// <paramref name="readFile"/> returns a file of the world directory by name, or null when it is missing.
        /// Stops at the first problem; the server's loader reports them all.
        /// </summary>
        public static bool TryLoadGrid(Func<string, byte[]?> readFile, out WorldGrid? grid, out string error)
        {
            grid = null;
            if (!TryReadManifest(readFile, out var manifest, out error))
                return false;

            var names = new List<string>();
            foreach (var file in manifest.Files)
                names.Add(file.Name);
            var listError = CheckFileList(names);
            if (listError != null)
                return Fail(WorldFiles.Manifest, listError, out error);

            byte[]? gridBytes = null;
            foreach (var file in manifest.Files)
            {
                var bytes = readFile(file.Name);
                if (bytes == null)
                    return Fail(file.Name, "listed in the manifest but missing", out error);
                var fileError = CheckFile(bytes, file.Bytes, file.Sha256);
                if (fileError != null)
                    return Fail(file.Name, fileError, out error);
                if (file.Name == WorldFiles.Grid)
                    gridBytes = bytes;
            }

            if (!WorldGridReader.TryRead(gridBytes!, out var decoded, out var gridError))
                return Fail(WorldFiles.Grid, gridError!, out error);
            if (!GeometryMatches(decoded!, manifest.CellSizeCm, manifest.WidthCells, manifest.HeightCells,
                    manifest.MinXCm, manifest.MinYCm, manifest.MaxXCm, manifest.MaxYCm))
                return Fail(WorldFiles.Grid, "grid geometry does not match the manifest's bounds, cell size or size in cells", out error);

            grid = decoded;
            error = string.Empty;
            return true;
        }

        private static bool TryReadManifest(Func<string, byte[]?> readFile, out Manifest manifest, out string error)
        {
            manifest = new Manifest();
            var bytes = readFile(WorldFiles.Manifest);
            if (bytes == null)
                return Fail(WorldFiles.Manifest, "not found", out error);
            if (!ConfigJson.TryParse(new UTF8Encoding(false).GetString(bytes), out var root, out var parseError))
                return Fail(WorldFiles.Manifest, parseError, out error);

            try
            {
                var format = String(root, "format");
                long version = Int(root, "formatVersion");
                if (format != WorldFiles.ManifestFormat)
                    return Fail(WorldFiles.Manifest, $"format '{format}', expected '{WorldFiles.ManifestFormat}'", out error);
                if (version != WorldFiles.ManifestVersion)
                    return Fail(WorldFiles.Manifest, $"format version {version}, this build reads {WorldFiles.ManifestVersion}", out error);

                manifest.CellSizeCm = (int)Int(root, "cellSizeCm");
                manifest.WidthCells = (int)Int(root, "widthCells");
                manifest.HeightCells = (int)Int(root, "heightCells");
                var bounds = Member(root, "bounds");
                manifest.MinXCm = (int)Int(bounds, "minXCm");
                manifest.MinYCm = (int)Int(bounds, "minYCm");
                manifest.MaxXCm = (int)Int(bounds, "maxXCm");
                manifest.MaxYCm = (int)Int(bounds, "maxYCm");
                var files = Member(root, "files");
                if (files.Kind != ConfigJsonKind.Array)
                    throw new FormatException("'files' must be an array");
                foreach (var file in files.Items)
                    manifest.Files.Add(new ListedFile(String(file, "name"), Long(file, "bytes"), String(file, "sha256")));
            }
            catch (FormatException e)
            {
                return Fail(WorldFiles.Manifest, e.Message, out error);
            }
            error = string.Empty;
            return true;
        }

        private static ConfigJsonValue Member(ConfigJsonValue parent, string name)
        {
            if (parent.Kind != ConfigJsonKind.Object || !parent.TryGetMember(name, out var value))
                throw new FormatException($"'{name}' is missing");
            return value;
        }

        private static string String(ConfigJsonValue parent, string name)
        {
            var value = Member(parent, name);
            if (value.Kind != ConfigJsonKind.String)
                throw new FormatException($"'{name}' must be a string");
            return value.Text;
        }

        private static long Int(ConfigJsonValue parent, string name)
        {
            long value = Long(parent, name);
            if (value < int.MinValue || value > int.MaxValue)
                throw new FormatException($"'{name}' must be a 32-bit integer");
            return value;
        }

        private static long Long(ConfigJsonValue parent, string name)
        {
            if (!Member(parent, name).TryGetInt64(out long value))
                throw new FormatException($"'{name}' must be an integer");
            return value;
        }

        private static bool Fail(string file, string message, out string error)
        {
            error = $"{file}: {message}";
            return false;
        }

        private sealed class Manifest
        {
            public int CellSizeCm;
            public int WidthCells;
            public int HeightCells;
            public int MinXCm;
            public int MinYCm;
            public int MaxXCm;
            public int MaxYCm;
            public readonly List<ListedFile> Files = new List<ListedFile>();
        }

        private readonly struct ListedFile
        {
            public ListedFile(string name, long bytes, string sha256)
            {
                Name = name;
                Bytes = bytes;
                Sha256 = sha256;
            }

            public string Name { get; }
            public long Bytes { get; }
            public string Sha256 { get; }
        }
    }
}
