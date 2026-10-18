#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Text;
using Shared.Data;
using UnityEngine;

namespace Game.Gameplay.Entities
{
    /// <summary>
    /// Loads the config tables the server loads (spec 1.8) with the portable <see cref="ConfigTableReader"/> and
    /// validates them the same way. In the editor the files come straight from the repository's <c>data/tables</c>.
    /// TODO(build): copy data/tables into StreamingAssets for player builds; until then a build looks for
    /// <c>StreamingAssets/tables</c> and fails with a clear error.
    /// </summary>
    public static class ClientConfig
    {
        public static string TablesDirectory =>
#if UNITY_EDITOR
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "data", "tables"));
#else
            Path.Combine(Application.streamingAssetsPath, "tables");
#endif

        public static bool TryLoad(out ConfigSet set, out string error)
        {
            var directory = TablesDirectory;
            var errors = new List<ValidationError>();
            set = ConfigTableReader.LoadAndValidate(ConfigTables.All, name =>
            {
                var path = Path.Combine(directory, name + ".json");
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }, errors);

            if (errors.Count == 0)
            {
                error = string.Empty;
                return true;
            }
            var text = new StringBuilder($"Config tables in {directory} are invalid (run src/Tools/DataValidator):");
            foreach (var e in errors)
                text.Append("\n  ").Append(e);
            error = text.ToString();
            return false;
        }
    }
}
