#nullable enable
using System.Collections.Generic;
using System.IO;
using Shared.World;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.WorldAuthoring.Editor
{
    /// <summary>
    /// The greybox of the first field zone (WP-M2-7, spec 12.8, user decision 2026-10-27), added to the M1 sample scene
    /// by <see cref="SampleWorldBuilder"/>: one zone of 400 x 400 m east of the city gate, four rings of rising monster
    /// level (regions <c>field1.ring1</c> to <c>field1.ring4</c> around the M1 combat area), ridges with passes between
    /// them, a road from the gate to the far end, the field boss on a low plateau at the far end, the mid-zone outpost,
    /// 84 monster spawn markers in camps of three, and resource node markers (data only; gathering is M3).
    /// Greybox only: flat colored boxes, every size and color a placeholder. Art replaces these objects in place in the
    /// scene; from then on the scene is edited by hand and this builder retires (rebuilding would drop the art).
    /// <para>
    /// Layout in meters (Unity X east, Z north; the gate at X = -10): ring 1 X -10..110, Z -80..80 around the M1 combat
    /// area (the tutorial ring of spec 12.3, demons, aliens and the imps on its outer edge); ring 2 the rest of X -10..190
    /// (spitters, the mid-zone outpost by the road at X 176); ring 3 X 190..290 (wolves, then lurkers); ring 4 X 290..390 (brutes,
    /// hellhounds, the warlord at X 370). Every camp's leash circle keeps clear of the outposts (WorldLoader.CheckConfigMarkers).
    /// </para>
    /// </summary>
    public static class FieldZoneGreybox
    {
        public const string MaterialFolder = "Assets/Scenes/Greybox";

        public const string MidOutpostNpc = "field1.npc.outpost.mid";
        public const string MidVendorNpc = "field1.npc.vendor.mid";
        public const string BossSpawn = "field1.spawn.warlord";

        /// <summary>A camp: three monsterSpawn markers of one type, named <c>field1.spawn.{type}.{camp}{1..3}</c>.</summary>
        private readonly struct Camp
        {
            public Camp(string type, char letter, float x, float z)
            {
                Type = type;
                Letter = letter;
                X = x;
                Z = z;
            }

            public string Type { get; }
            public char Letter { get; }
            public float X { get; }
            public float Z { get; }
        }

        private static readonly Camp[] Camps =
        {
            // Ring 1 (band 1 and the imps, level 8): two more camps of demons and aliens in the strips north and south of
            // the M1 combat area, the imps on the ring's east edge.
            new Camp("demon", 'a', 5f, 58f), new Camp("demon", 'b', 5f, -58f),
            new Camp("alien", 'a', 38f, 62f), new Camp("alien", 'b', 38f, -62f),
            new Camp("imp", 'a', 72f, 52f), new Camp("imp", 'b', 72f, -52f), new Camp("imp", 'c', 95f, 18f), new Camp("imp", 'd', 95f, -18f),
            // Ring 2 (level 12): the spitters east of ring 1, clear of the mid-zone outpost at X 175.
            new Camp("spitter", 'a', 135f, 125f), new Camp("spitter", 'b', 135f, -125f), new Camp("spitter", 'c', 150f, 50f),
            new Camp("spitter", 'd', 150f, -50f),
            // Ring 3: wolves (16) on its near half, lurkers (20) on its far half.
            new Camp("wolf", 'a', 220f, 125f), new Camp("wolf", 'b', 220f, 42f), new Camp("wolf", 'c', 220f, -42f), new Camp("wolf", 'd', 220f, -125f),
            new Camp("lurker", 'a', 265f, 155f), new Camp("lurker", 'b', 265f, 65f), new Camp("lurker", 'c', 265f, -65f),
            new Camp("lurker", 'd', 265f, -155f),
            // Ring 4: brutes (24) on its near half, hellhounds (28) on its far half around the warlord's plateau.
            new Camp("brute", 'a', 315f, 130f), new Camp("brute", 'b', 315f, 42f), new Camp("brute", 'c', 315f, -42f), new Camp("brute", 'd', 315f, -130f),
            new Camp("hellhound", 'a', 352f, 165f), new Camp("hellhound", 'b', 352f, 85f), new Camp("hellhound", 'c', 352f, -85f),
            new Camp("hellhound", 'd', 352f, -165f),
        };

        /// <summary>The three points of a camp, around its center.</summary>
        private static readonly Vector2[] CampPoints = { new Vector2(-3f, -2f), new Vector2(3f, -2f), new Vector2(0f, 3f) };

        /// <summary>Ridges between the rings: (x0, z0, x1, z1) segments, 2 m thick, with passes left open between segments.</summary>
        private static readonly Vector4[] Ridges =
        {
            // Ring 1 / ring 2: north and south edges (passes at X 30..40 and 80..90), east edge (passes at Z -6..6, +-40..50).
            new Vector4(-10f, 80f, 30f, 80f), new Vector4(40f, 80f, 80f, 80f), new Vector4(90f, 80f, 110f, 80f),
            new Vector4(-10f, -80f, 30f, -80f), new Vector4(40f, -80f, 80f, -80f), new Vector4(90f, -80f, 110f, -80f),
            new Vector4(110f, 50f, 110f, 80f), new Vector4(110f, 6f, 110f, 40f), new Vector4(110f, -40f, 110f, -6f), new Vector4(110f, -80f, 110f, -50f),
            // Ring 2 / ring 3 and ring 3 / ring 4: passes at Z -6..6 (the road) and +-100..110.
            new Vector4(190f, 110f, 190f, 200f), new Vector4(190f, 6f, 190f, 100f), new Vector4(190f, -100f, 190f, -6f), new Vector4(190f, -200f, 190f, -110f),
            new Vector4(290f, 110f, 290f, 200f), new Vector4(290f, 6f, 290f, 100f), new Vector4(290f, -100f, 290f, -6f), new Vector4(290f, -200f, 290f, -110f),
        };

        /// <summary>
        /// The tutorial gate (spec 12.3 R-12.3-01, WP-M2-10): every pass of the ring 1 / ring 2 ridge above, (x0, z0, x1, z1)
        /// across its opening. The gate is the region's level (<c>RegionDef.minLevel</c> of <c>field1.ring2</c>, R-2.3-06), not
        /// these objects: they are its look, posts and a bar with no collider (the export does not change).
        /// </summary>
        private static readonly Vector4[] GatePasses =
        {
            new Vector4(30f, 80f, 40f, 80f), new Vector4(80f, 80f, 90f, 80f),
            new Vector4(30f, -80f, 40f, -80f), new Vector4(80f, -80f, 90f, -80f),
            new Vector4(110f, -6f, 110f, 6f), new Vector4(110f, 40f, 110f, 50f), new Vector4(110f, -50f, 110f, -40f),
        };

        /// <summary>The region the tutorial gate opens into.</summary>
        public const string GateRegion = "field1.ring2";

        /// <summary>Rocks (blockers) for cover and readability: x, z, size.</summary>
        private static readonly Vector3[] Rocks =
        {
            new Vector3(60f, 20f, 3f), new Vector3(60f, -25f, 4f), new Vector3(25f, 45f, 3f), new Vector3(85f, 65f, 4f),
            new Vector3(40f, 140f, 5f), new Vector3(70f, -150f, 6f), new Vector3(165f, 160f, 4f), new Vector3(120f, -60f, 4f),
            new Vector3(240f, 90f, 5f), new Vector3(245f, -95f, 5f), new Vector3(205f, 175f, 4f), new Vector3(280f, 15f, 4f),
            new Vector3(330f, 90f, 6f), new Vector3(335f, -95f, 6f), new Vector3(300f, 180f, 5f), new Vector3(375f, 120f, 5f),
        };

        /// <summary>
        /// Resource nodes (spec 5.2; data/tables/GatherNodes.json names the node type of each, WP-M3-4): id suffix, x, z. The
        /// two gem veins of ring 3 (gem1, gem2) sit between its wolf and lurker camps, clear of the rocks.
        /// </summary>
        private static readonly (string Id, float X, float Z)[] Nodes =
        {
            ("ring1.a", 20f, 70f), ("ring1.b", 85f, -70f),
            ("ring2.a", 30f, 120f), ("ring2.b", 60f, -170f), ("ring2.c", 170f, 150f), ("ring2.d", 125f, -95f),
            ("ring3.a", 200f, 160f), ("ring3.b", 250f, 10f), ("ring3.c", 205f, -170f),
            ("ring3.gem1", 242f, 115f), ("ring3.gem2", 242f, -115f),
            ("ring4.a", 300f, 160f), ("ring4.b", 330f, 0f), ("ring4.c", 380f, -180f),
        };

        public static void Build()
        {
            var zone = new GameObject("Field Zone").transform;
            var ground = new GameObject("Ground").transform;
            ground.SetParent(zone, false);
            var ring1 = Material("Ring1", new Color(0.42f, 0.55f, 0.36f));
            var ring2 = Material("Ring2", new Color(0.5f, 0.55f, 0.33f));
            var ring3 = Material("Ring3", new Color(0.5f, 0.47f, 0.33f));
            var ring4 = Material("Ring4", new Color(0.43f, 0.37f, 0.33f));
            var road = Material("Road", new Color(0.66f, 0.58f, 0.44f));
            var stone = Material("Stone", new Color(0.52f, 0.5f, 0.48f));
            var hut = Material("Hut", new Color(0.55f, 0.4f, 0.27f));

            // Regions and their ground, one box per volume (top at height 0). m1.combat (X -10..50, Z -30..30) keeps the
            // M1 sample's ground; ring 1 wraps around it.
            Area(zone, ground, "field1.ring1", ring1, -10f, 30f, 50f, 80f);
            Area(zone, ground, "field1.ring1", ring1, -10f, -80f, 50f, -30f);
            Area(zone, ground, "field1.ring1", ring1, 50f, -80f, 110f, 80f);
            Area(zone, ground, "field1.ring2", ring2, -10f, 80f, 110f, 200f);
            Area(zone, ground, "field1.ring2", ring2, -10f, -200f, 110f, -80f);
            Area(zone, ground, "field1.ring2", ring2, 110f, -200f, 190f, 200f);
            Area(zone, ground, "field1.ring3", ring3, 190f, -200f, 290f, 200f);
            Area(zone, ground, "field1.ring4", ring4, 290f, -200f, 390f, 200f);

            // The road from the gate to the warlord's ramp, 2 cm above the ground (a strip of another color; same height
            // for the server, which stores whole cm).
            var roadStrip = Box("Road", new Vector3(170f, 0.01f, 0f), new Vector3(360f, 0.02f, 6f), blocker: false, ground);
            roadStrip.GetComponent<Renderer>().sharedMaterial = road;

            // The warlord's plateau at the far end, 1 m up, with a ramp from the road (spec 2.3: ground heights per cell).
            var plateau = Box("Warlord Plateau", new Vector3(370f, 0.5f, 0f), new Vector3(30f, 1f, 40f), blocker: false, ground);
            plateau.GetComponent<Renderer>().sharedMaterial = stone;
            float rampRun = 15f, rampRise = 1f, rampThickness = 0.5f;
            float rampAngle = Mathf.Atan2(rampRise, rampRun) * Mathf.Rad2Deg;
            var ramp = Box("Warlord Ramp", new Vector3(347.5f, rampRise / 2f - rampThickness / 2f, 0f),
                new Vector3(Mathf.Sqrt(rampRun * rampRun + rampRise * rampRise), rampThickness, 10f), blocker: false, ground);
            ramp.transform.rotation = Quaternion.Euler(0f, 0f, rampAngle);
            ramp.GetComponent<Renderer>().sharedMaterial = stone;

            // Blockers: the zone's edge (the world ends there anyway: no ground beyond), the ridges between rings, rocks.
            var walls = new GameObject("Blockers").transform;
            walls.SetParent(zone, false);
            Wall(walls, stone, "Edge North", -10f, 200f, 390f, 200f, 3f);
            Wall(walls, stone, "Edge South", -10f, -200f, 390f, -200f, 3f);
            Wall(walls, stone, "Edge East", 390f, -200f, 390f, 200f, 3f);
            Wall(walls, stone, "Edge West North", -10f, 30f, -10f, 200f, 3f);
            Wall(walls, stone, "Edge West South", -10f, -200f, -10f, -30f, 3f);
            for (int i = 0; i < Ridges.Length; i++)
            {
                var r = Ridges[i];
                Wall(walls, stone, $"Ridge {i + 1:D2}", r.x, r.y, r.z, r.w, 2.5f);
            }
            for (int i = 0; i < Rocks.Length; i++)
            {
                var rock = Box($"Rock {i + 1:D2}", new Vector3(Rocks[i].x, 1f, Rocks[i].y), new Vector3(Rocks[i].z, 2f, Rocks[i].z), blocker: true, walls);
                rock.GetComponent<Renderer>().sharedMaterial = stone;
            }

            // The tutorial gate's look at every ring 1 / ring 2 pass (WP-M2-10): shown below the level of ring 2.
            var gateMaterial = Material("Gate", new Color(0.75f, 0.22f, 0.18f));
            var gates = new GameObject("Tutorial Gates").transform;
            gates.SetParent(zone, false);
            for (int i = 0; i < GatePasses.Length; i++)
                GateLook(gates, gateMaterial, $"Gate {i + 1:D2}", GatePasses[i]);

            // The mid-zone outpost (spec 12.8: an outpost NPC and a vendor; both share the gate outpost's NpcDef rows): a hut
            // just north of the road before the ring 3 pass, the two NPCs in front of it facing the road.
            var outpost = new GameObject("Mid Outpost").transform;
            outpost.SetParent(zone, false);
            var hall = Box("Outpost Hut", new Vector3(176f, 1.5f, 18f), new Vector3(8f, 3f, 5f), blocker: true, outpost);
            hall.GetComponent<Renderer>().sharedMaterial = hut;
            SampleWorldBuilder.NpcMarker("Outpost NPC", MidOutpostNpc, "m1.outpost", new Vector3(173f, 0f, 11f), yaw: 180f, outpost);
            SampleWorldBuilder.NpcMarker("Vendor NPC", MidVendorNpc, "m1.vendor", new Vector3(179f, 0f, 11f), yaw: 180f, outpost);

            // Monster camps (SpawnPoints rows name every marker), all facing the gate.
            var spawns = new GameObject("Spawns").transform;
            spawns.SetParent(zone, false);
            foreach (var camp in Camps)
            {
                for (int i = 0; i < CampPoints.Length; i++)
                {
                    string id = $"field1.spawn.{camp.Type}.{camp.Letter}{i + 1}";
                    var at = new Vector3(camp.X + CampPoints[i].x, 0f, camp.Z + CampPoints[i].y);
                    SampleWorldBuilder.MonsterSpawn($"Spawn {camp.Type} {camp.Letter}{i + 1}", id, at, yaw: 270f, spawns);
                }
            }
            SampleWorldBuilder.MonsterSpawn("Spawn Warlord", BossSpawn, new Vector3(372f, 1f, 0f), yaw: 270f, spawns);

            var nodes = new GameObject("Resource Nodes").transform;
            nodes.SetParent(zone, false);
            foreach (var (id, x, z) in Nodes)
            {
                var marker = new GameObject("Node " + id).AddComponent<ServerMarker>();
                marker.transform.SetParent(nodes, false);
                marker.markerId = "field1.node." + id;
                marker.kind = MarkerKind.ResourceNode;
                marker.transform.position = new Vector3(x, 0f, z);
            }
        }

        /// <summary>A region volume and its ground box over the rectangle [x0, x1] x [z0, z1].</summary>
        private static void Area(Transform zone, Transform ground, string regionId, Material material, float x0, float z0, float x1, float z1)
        {
            var center = new Vector3((x0 + x1) / 2f, 0f, (z0 + z1) / 2f);
            var size = new Vector3(x1 - x0, 10f, z1 - z0);
            var volume = new GameObject($"Region {regionId} ({x0},{z0})").AddComponent<RegionVolume>();
            volume.transform.SetParent(zone, false);
            volume.regionId = regionId;
            volume.transform.position = center;
            volume.size = size;
            var box = Box($"Ground {regionId} ({x0},{z0})", new Vector3(center.x, -0.5f, center.z), new Vector3(size.x, 1f, size.z),
                blocker: false, ground);
            box.GetComponent<Renderer>().sharedMaterial = material;
        }

        /// <summary>
        /// The look of the gate across a pass: a post at each end and a bar between them at head height, no colliders (the
        /// ground ray and the blockers of the export never see them), with a <see cref="LevelGateVisual"/> for its region.
        /// </summary>
        private static void GateLook(Transform parent, Material material, string name, Vector4 pass)
        {
            var gate = new GameObject(name).AddComponent<LevelGateVisual>();
            gate.regionId = GateRegion;
            gate.transform.SetParent(parent, false);
            var a = new Vector3(pass.x, 0f, pass.y);
            var b = new Vector3(pass.z, 0f, pass.w);
            const float postSize = 0.4f, postHeight = 3f, barHeight = 2.4f, barThickness = 0.25f;
            Part(gate.transform, material, "Post A", a + Vector3.up * (postHeight / 2f), new Vector3(postSize, postHeight, postSize));
            Part(gate.transform, material, "Post B", b + Vector3.up * (postHeight / 2f), new Vector3(postSize, postHeight, postSize));
            var span = b - a;
            var barSize = Mathf.Abs(span.x) > 0f
                ? new Vector3(Mathf.Abs(span.x), barThickness, barThickness)
                : new Vector3(barThickness, barThickness, Mathf.Abs(span.z));
            Part(gate.transform, material, "Bar", (a + b) / 2f + Vector3.up * barHeight, barSize);
        }

        private static void Part(Transform parent, Material material, string name, Vector3 position, Vector3 size)
        {
            var part = GameObject.CreatePrimitive(PrimitiveType.Cube);
            part.name = name;
            Object.DestroyImmediate(part.GetComponent<Collider>());
            part.transform.SetParent(parent, false);
            part.transform.position = position;
            part.transform.localScale = size;
            part.GetComponent<Renderer>().sharedMaterial = material;
        }

        /// <summary>An axis-aligned blocker wall 2 m thick along a segment.</summary>
        private static void Wall(Transform parent, Material material, string name, float x0, float z0, float x1, float z1, float height)
        {
            const float thickness = 2f;
            float lengthX = Mathf.Abs(x1 - x0), lengthZ = Mathf.Abs(z1 - z0);
            var size = new Vector3(lengthX > 0f ? lengthX : thickness, height, lengthZ > 0f ? lengthZ : thickness);
            var wall = Box(name, new Vector3((x0 + x1) / 2f, height / 2f, (z0 + z1) / 2f), size, blocker: true, parent);
            wall.GetComponent<Renderer>().sharedMaterial = material;
        }

        private static GameObject Box(string name, Vector3 position, Vector3 size, bool blocker, Transform parent) =>
            SampleWorldBuilder.Box(name, position, size, blocker, parent);

        private static Material Material(string name, Color color)
        {
            string path = $"{MaterialFolder}/{name}.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                Directory.CreateDirectory(MaterialFolder);
                var shader = GraphicsSettings.defaultRenderPipeline != null
                    ? GraphicsSettings.defaultRenderPipeline.defaultShader
                    : Shader.Find("Standard");
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, path);
            }
            material.SetColor("_BaseColor", color);
            material.color = color;
            // URP's Lit validation (BaseShaderGUI.SetMaterialKeywords) copies _BaseColor into the legacy _Color whenever
            // the editor loads the material; do the same here, so loading it does not change and re-save it (QA wave 1 O7).
            if (material.HasProperty("_Color") && material.HasProperty("_BaseColor"))
                material.SetColor("_Color", material.GetColor("_BaseColor"));
            EditorUtility.SetDirty(material);
            return material;
        }

        /// <summary>Every monsterSpawn marker id the greybox places, with its monster type (for SpawnPoints rows and tests).</summary>
        public static IEnumerable<(string MarkerId, string Type)> SpawnMarkers()
        {
            foreach (var camp in Camps)
            {
                for (int i = 0; i < CampPoints.Length; i++)
                    yield return ($"field1.spawn.{camp.Type}.{camp.Letter}{i + 1}", camp.Type);
            }
            yield return (BossSpawn, "warlord");
        }
    }
}
