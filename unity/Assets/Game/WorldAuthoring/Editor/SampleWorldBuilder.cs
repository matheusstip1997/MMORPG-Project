#nullable enable
using System.IO;
using Shared.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Game.WorldAuthoring.Editor
{
    /// <summary>
    /// Builds the M1 sample scene (backlog C2, spec 12.4): a small safe city (region <c>m1.city</c>, area type
    /// normalCity) with walls and a gate, next to a small combat area (<c>m1.combat</c>, openWorld) with rocks and a
    /// raised platform, the city's player spawn and two monster spawns. Placeholder geometry; the scene is committed,
    /// rebuilding it is only needed when this layout changes. Batch mode:
    /// <code>
    /// Unity.exe -batchmode -quit -projectPath unity -executeMethod Game.WorldAuthoring.Editor.SampleWorldBuilder.Build
    /// </code>
    /// Layout in meters (Unity X east, Z north): ground X -50..50, Z -30..30; the city is X -50..-10, the combat area
    /// X -10..50; the city's east wall stands on X = -10 with a 4 m gate at Z -2..2.
    /// </summary>
    public static class SampleWorldBuilder
    {
        public const string ScenePath = "Assets/Scenes/M1Sample.unity";
        public const string SpawnMarkerId = "m1.spawn.city";
        public const string MonsterSpawnA = "m1.spawn.combat.a";
        public const string MonsterSpawnB = "m1.spawn.combat.b";

        [MenuItem("Tools/World/Rebuild M1 Sample Scene")]
        public static void Build()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var settings = new GameObject("WorldExportSettings").AddComponent<WorldExportSettings>();
            settings.worldId = "m1";
            settings.cellSizeCm = 50;    // placeholder walkability resolution
            settings.agentRadiusCm = 40; // placeholder until character size is defined

            var light = new GameObject("Sun").AddComponent<Light>();
            light.type = LightType.Directional;
            light.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            Box("Ground", new Vector3(0f, -0.5f, 0f), new Vector3(100f, 1f, 60f), blocker: false);

            Region("Region City", "m1.city", new Vector3(-30f, 0f, 0f), new Vector3(40f, 10f, 60f));
            Region("Region Combat", "m1.combat", new Vector3(20f, 0f, 0f), new Vector3(60f, 10f, 60f));

            var city = new GameObject("City").transform;
            Box("East Wall South", new Vector3(-10f, 1.5f, -16f), new Vector3(1f, 3f, 28f), blocker: true, city);
            Box("East Wall North", new Vector3(-10f, 1.5f, 16f), new Vector3(1f, 3f, 28f), blocker: true, city);
            Box("Hall", new Vector3(-35f, 2f, 10f), new Vector3(8f, 4f, 6f), blocker: true, city);
            var shop = Box("Shop", new Vector3(-30f, 2f, -12f), new Vector3(10f, 4f, 4f), blocker: true, city);
            shop.transform.rotation = Quaternion.Euler(0f, 90f, 0f); // exercises the 90-degree rule

            var combat = new GameObject("Combat Area").transform;
            Box("Rock A", new Vector3(10f, 1f, 8f), new Vector3(3f, 2f, 3f), blocker: true, combat);
            Box("Rock B", new Vector3(25f, 1f, -10f), new Vector3(4f, 2f, 4f), blocker: true, combat);
            Box("Rock C", new Vector3(35f, 1f, 15f), new Vector3(2f, 2f, 5f), blocker: true, combat);
            Box("Platform", new Vector3(40f, 0.25f, -20f), new Vector3(6f, 0.5f, 6f), blocker: false, combat); // walkable, 50 cm up

            var spawn = new GameObject("Spawn City").AddComponent<ServerMarker>();
            spawn.markerId = SpawnMarkerId;
            spawn.kind = MarkerKind.PlayerSpawn;
            spawn.transform.SetPositionAndRotation(new Vector3(-30f, 0f, 0f), Quaternion.Euler(0f, 90f, 0f)); // facing the gate

            // Monster spawns (backlog F), named by data/tables/SpawnPoints.json: one a few meters past the gate, facing it,
            // 32 m from the player spawn (just outside the 30 m interest area, so a player sees it after a few steps toward
            // the gate); one further into the combat area.
            MonsterSpawn("Monster Spawn A", MonsterSpawnA, new Vector3(2f, 0f, 4f), yaw: 270f, combat);
            MonsterSpawn("Monster Spawn B", MonsterSpawnB, new Vector3(20f, 0f, 2f), yaw: 270f, combat);

            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath)!);
            EditorSceneManager.SaveScene(scene, ScenePath);
            Debug.Log($"[SampleWorld] saved {ScenePath}");
        }

        private static GameObject Box(string name, Vector3 position, Vector3 size, bool blocker, Transform? parent = null)
        {
            var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.name = name;
            if (parent != null)
                box.transform.SetParent(parent, worldPositionStays: false);
            box.transform.position = position;
            box.transform.localScale = size;
            if (blocker)
                box.AddComponent<ServerBlocker>(); // unit box in local space = the cube
            return box;
        }

        private static void MonsterSpawn(string name, string markerId, Vector3 position, float yaw, Transform parent)
        {
            var marker = new GameObject(name).AddComponent<ServerMarker>();
            marker.transform.SetParent(parent, worldPositionStays: false);
            marker.markerId = markerId;
            marker.kind = MarkerKind.MonsterSpawn;
            marker.transform.SetPositionAndRotation(position, Quaternion.Euler(0f, yaw, 0f));
        }

        private static void Region(string name, string regionId, Vector3 position, Vector3 size)
        {
            var volume = new GameObject(name).AddComponent<RegionVolume>();
            volume.regionId = regionId;
            volume.transform.position = position;
            volume.size = size;
        }
    }
}
