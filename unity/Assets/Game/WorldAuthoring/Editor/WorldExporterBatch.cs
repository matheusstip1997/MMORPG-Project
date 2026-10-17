#nullable enable
using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Game.WorldAuthoring.Editor
{
    /// <summary>
    /// Headless entry point for CI and the command line (spec 11.6 batch mode):
    /// <code>
    /// Unity.exe -batchmode -quit -projectPath unity -executeMethod Game.WorldAuthoring.Editor.WorldExporterBatch.Export
    ///     [-worldScene Assets/Scenes/M1Sample.unity] [-worldOut ../data/world/m1] [-worldReport Logs/world-export-report.json]
    /// </code>
    /// Relative paths are resolved against the Unity project folder. The default output is <c>../data/world/&lt;worldId&gt;</c>.
    /// Writes the JSON report always and the world files only when the validator found no error; exits 0 or 1.
    /// </summary>
    public static class WorldExporterBatch
    {
        public const string DefaultScene = "Assets/Scenes/M1Sample.unity";
        public const string DefaultReport = "Logs/world-export-report.json";

        public static void Export()
        {
            int code;
            try
            {
                code = Run(Environment.GetCommandLineArgs());
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                code = 2;
            }
            EditorApplication.Exit(code);
        }

        [MenuItem("Tools/World/Export M1 Sample World")]
        public static void ExportFromMenu()
        {
            int code = Run(Array.Empty<string>());
            EditorUtility.DisplayDialog("World export", code == 0 ? "Export written." : "Export failed; see the Console.", "OK");
        }

        /// <summary>Exports a scene with the given command-line options; returns the process exit code.</summary>
        public static int Run(string[] args)
        {
            var scenePath = Option(args, "-worldScene") ?? DefaultScene;
            var reportPath = ProjectPath(Option(args, "-worldReport") ?? DefaultReport);

            // A scene already open in the editor is exported as it is loaded (unsaved edits included) and left open.
            var loaded = UnityEngine.SceneManagement.SceneManager.GetSceneByPath(scenePath);
            bool opened = !loaded.isLoaded;
            var scene = opened ? EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive) : loaded;
            WorldExportResult result;
            try
            {
                result = WorldExporter.Export(scene);
            }
            finally
            {
                if (opened)
                    EditorSceneManager.CloseScene(scene, removeScene: true);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            File.WriteAllBytes(reportPath, WorldExporter.ReportJson(result));
            foreach (var error in result.Errors)
                Debug.LogError($"[WorldExport] {error}");

            if (!result.Ok)
            {
                Debug.LogError($"[WorldExport] {scenePath}: {result.Errors.Count} error(s); report {reportPath}");
                return 1;
            }

            var outDirectory = ProjectPath(Option(args, "-worldOut") ?? Path.Combine("..", "data", "world", result.WorldId));
            WorldExporter.WriteFiles(result, outDirectory);
            Debug.Log($"[WorldExport] {scenePath} -> {outDirectory}: {result.WidthCells} x {result.HeightCells} cells, " +
                      $"{result.WalkableCells} walkable, {result.RegionCount} region(s), {result.MarkerCount} marker(s), " +
                      $"{result.BlockerCount} blocker(s)");
            return 0;
        }

        private static string? Option(string[] args, string name)
        {
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] == name)
                    return args[i + 1];
            }
            return null;
        }

        private static string ProjectPath(string path) =>
            Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(Path.GetDirectoryName(Application.dataPath)!, path));
    }
}
