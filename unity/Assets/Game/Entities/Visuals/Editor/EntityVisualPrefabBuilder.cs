#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Shared.Data.Tables;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEngine;

namespace Game.Gameplay.Entities.Visuals.Editor
{
    /// <summary>
    /// Builds the M1 placeholder visuals from the third-party art (THIRD_PARTY_LICENSES.md) into
    /// <c>Assets/Content/Visuals</c> and registers them as Addressables under the addresses the
    /// <see cref="EntityVisualDef"/> table uses: one prefab per root class (a VRoid avatar), one per monster, a wrapper
    /// prefab per weapon (the KayKit model with its grip offset, so it sits in a humanoid right or left hand), an arrow
    /// made of primitives (our own placeholder, spec 3.2), and an animation set per body (Quaternius clips). Our own art
    /// replaces these by new prefabs at the same addresses.
    /// Menu: MMORPG / Visuals / Rebuild Visual Prefabs. Batch mode:
    /// <c>Unity.exe -batchmode -projectPath unity -executeMethod Game.Gameplay.Entities.Visuals.Editor.EntityVisualPrefabBuilder.Build -quit</c>.
    /// Generated: edit this builder instead of the prefabs.
    /// </summary>
    public static class EntityVisualPrefabBuilder
    {
        public const string OutputFolder = "Assets/Content/Visuals";
        public const string GroupName = "Visuals";

        private const string ThirdParty = "Assets/ThirdParty";
        private const string Ual1 = ThirdParty + "/QuaterniusUAL/AnimationLibrary_Unity_Standard.fbx";
        private const string Ual2 = ThirdParty + "/QuaterniusUAL2/UAL2_Standard.fbx";
        private const string Monsters = ThirdParty + "/QuaterniusCuteMonsters";
        private const string Weapons = ThirdParty + "/KayKitAdventurers/Weapons";
        private const string Avatars = ThirdParty + "/VRoidSamples";

        // Fallback reference speeds when a clip has no root motion to measure (art data, see AnimationSet).
        private const float DefaultWalkCmPerSec = 150f;
        private const float DefaultRunCmPerSec = 400f;

        // KayKit weapons are sized for KayKit's own characters; this fits them to a 1.6 m VRoid avatar.
        private const float WeaponScale = 0.55f;

        /// <summary>The arrow of <c>EntityVisualDef</c> row "arrow" and the height it flies at (chest height of the avatars).</summary>
        private const string ArrowAddress = "Visuals/Projectiles/Arrow";
        private const float ArrowHeightM = 1.3f;

        /// <summary>The alien's spit of <c>EntityVisualDef</c> row "alienSpit" and the height it flies at (the alien's mouth).</summary>
        private const string SpitAddress = "Visuals/Projectiles/AlienSpit";
        private const float SpitHeightM = 0.9f;

        /// <summary>The Mage's Magic Bolt of <c>EntityVisualDef</c> row "magicBolt" (backlog E); it flies at chest height.</summary>
        private const string MagicBoltAddress = "Visuals/Projectiles/MagicBolt";
        /// <summary>The Cyclops boss's rock of <c>EntityVisualDef</c> row "cyclopsRock" and the height it flies at (the boss's hand).</summary>
        private const string RockAddress = "Visuals/Projectiles/CyclopsRock";
        private const float RockHeightM = 1.6f;

        /// <summary>The ground drop of the <c>EntityVisualDef</c> rows "drop.*" (<c>ItemDef.visualId</c>, backlog G).</summary>
        private const string GroundDropAddress = "Visuals/Items/GroundDrop";

        private sealed class WeaponArt
        {
            public WeaponArt(string address, string model, Vector3 gripPosition, Vector3 gripEuler)
            {
                Address = address;
                Model = model;
                GripPosition = gripPosition;
                GripEuler = gripEuler;
            }

            public string Address { get; }
            public string Model { get; }

            /// <summary>Model pose in the hand bone's space (VRoid hand bones are world-aligned in the T-pose).</summary>
            public Vector3 GripPosition { get; }
            public Vector3 GripEuler { get; }
        }

        private sealed class CharacterArt
        {
            public CharacterArt(string address, string avatar, string attack, string? channelLoop, params ActionArt[] actions)
            {
                Address = address;
                Avatar = avatar;
                Attack = attack;
                ChannelLoop = channelLoop;
                Actions = actions;
            }

            public string Address { get; }
            public string Avatar { get; }

            /// <summary>Default action clip (Universal Animation Library take).</summary>
            public string Attack { get; }
            public string? ChannelLoop { get; }

            /// <summary>
            /// One entry per action of the class's kit (backlog E), even when takes repeat, so the final animations replace
            /// data and assets only (PLACEHOLDER takes until then).
            /// </summary>
            public ActionArt[] Actions { get; }
        }

        /// <summary>The clip of one action: a take of the Universal Animation Library (part 1 or 2).</summary>
        private sealed class ActionArt
        {
            public ActionArt(string actionId, string library, string take, string? channelLoop = null)
            {
                ActionId = actionId;
                Library = library;
                Take = take;
                ChannelLoop = channelLoop;
            }

            public string ActionId { get; }

            /// <summary><see cref="Ual1"/> or <see cref="Ual2"/>.</summary>
            public string Library { get; }
            public string Take { get; }

            /// <summary>A loop of the same library for the Channel phase, or null.</summary>
            public string? ChannelLoop { get; }
        }

        private sealed class MonsterArt
        {
            public MonsterArt(string address, string model, float yawDegrees, params (string ActionId, string Take)[] actions)
            {
                Address = address;
                Model = model;
                YawDegrees = yawDegrees;
                Actions = actions;
            }

            public string Address { get; }
            public string Model { get; }

            /// <summary>Turn so the model faces +Z like the entity root.</summary>
            public float YawDegrees { get; }

            /// <summary>The model's take for each of the monster's attacks (<c>MonsterDef.attacks</c>); others play Bite_Front.</summary>
            public (string ActionId, string Take)[] Actions { get; }
        }

        private static readonly WeaponArt[] WeaponList =
        {
            // VRoid hand bones in the T-pose: fingers along +X (right hand) or -X (left), palm down, so a fist's axis is
            // +Z. The KayKit models stand along +Y with the grip at the origin, at about twice our scale.
            new WeaponArt("Visuals/Weapons/Sword2H", "sword_2handed.gltf", new Vector3(0.05f, -0.02f, 0f), new Vector3(90f, 0f, 0f)),
            new WeaponArt("Visuals/Weapons/Crossbow", "crossbow_2handed.gltf", new Vector3(0.05f, -0.02f, 0f), new Vector3(0f, 0f, 0f)),
            new WeaponArt("Visuals/Weapons/Staff", "staff.gltf", new Vector3(0.05f, -0.02f, 0f), new Vector3(90f, 0f, 0f)),
            new WeaponArt("Visuals/Weapons/Wand", "wand.gltf", new Vector3(0.05f, -0.02f, 0f), new Vector3(90f, 0f, 0f)),
            // Strapped to the left forearm, its face toward the back of the hand.
            new WeaponArt("Visuals/Weapons/RoundShield", "shield_round.gltf", new Vector3(0.1f, 0.06f, 0f), new Vector3(-90f, 0f, 0f)),
        };

        private static readonly CharacterArt[] CharacterList =
        {
            // PLACEHOLDER takes for the M1 kits (ActionBindings rows); the user's animations replace them.
            new CharacterArt("Visuals/Characters/Warrior", "AvatarSample_D.vrm", "Sword_Attack", null,
                new ActionArt("attack.warrior", Ual1, "Sword_Attack"),
                new ActionArt("skill.warrior.slash", Ual2, "Sword_Regular_A"),
                new ActionArt("skill.warrior.headCut", Ual2, "Sword_Regular_B"),
                new ActionArt("skill.warrior.piercingAttack", Ual2, "Sword_Regular_C"),
                new ActionArt("skill.warrior.jumpStrike", Ual2, "Sword_Regular_Combo")),
            new CharacterArt("Visuals/Characters/Archer", "AvatarSample_E.vrm", "Pistol_Shoot", null,
                new ActionArt("attack.archer", Ual1, "Pistol_Shoot"),
                new ActionArt("skill.archer.piercingShot", Ual1, "Pistol_Shoot"),
                new ActionArt("skill.archer.multiShot", Ual1, "Pistol_Shoot"),
                new ActionArt("skill.archer.heavyShot", Ual1, "Pistol_Shoot", "Pistol_Aim_Neutral"),
                new ActionArt("skill.archer.backStep", Ual1, "Roll_RM"),
                new ActionArt("skill.archer.dash", Ual1, "Roll_RM")),
            new CharacterArt("Visuals/Characters/Mage", "AvatarSample_F.vrm", "Spell_Simple_Shoot", "Spell_Simple_Idle_Loop",
                new ActionArt("attack.mage", Ual1, "Spell_Simple_Shoot")),
            new CharacterArt("Visuals/Characters/Assist", "AvatarSample_G.vrm", "Spell_Simple_Shoot", "Spell_Simple_Idle_Loop",
                new ActionArt("attack.assist", Ual1, "Sword_Attack")),
        };

        private static readonly MonsterArt[] MonsterList =
        {
            // The Cute Monsters takes: Bite_Front, Bite_InPlace, Jump, Yes, No, Dance, HitRecieve, Death, Idle, Walk.
            new MonsterArt("Visuals/Monsters/Demon", "Demon.fbx", 0f,
                ("monster.demon.claw", "Bite_InPlace"), ("monster.demon.pounce", "Jump")),
            new MonsterArt("Visuals/Monsters/Alien", "Alien.fbx", 0f,
                ("monster.alien.slap", "Bite_InPlace"), ("monster.alien.spit", "Yes")),
            // The boss (WP10): one entry per attack, so its real clips replace data only. Placeholder takes.
            new MonsterArt("Visuals/Monsters/Cyclops", "Cyclops.fbx", 0f,
                ("monster.cyclops.stomp", "Jump"), ("monster.cyclops.slam", "Bite_Front"), ("monster.cyclops.sweep", "No"),
                ("monster.cyclops.swipe", "Bite_InPlace"), ("monster.cyclops.rockThrow", "Yes")),
        };

        [MenuItem("MMORPG/Visuals/Rebuild Visual Prefabs")]
        public static void Build()
        {
            ReimportFailedModels();
            Directory.CreateDirectory(OutputFolder + "/Characters");
            Directory.CreateDirectory(OutputFolder + "/Monsters");
            Directory.CreateDirectory(OutputFolder + "/Weapons");
            Directory.CreateDirectory(OutputFolder + "/Projectiles");
            Directory.CreateDirectory(OutputFolder + "/Items");
            Directory.CreateDirectory(OutputFolder + "/AnimationSets");
            Directory.CreateDirectory(OutputFolder + "/Materials");
            AssetDatabase.Refresh();

            var built = new List<(string Address, string Path)>();
            foreach (var weapon in WeaponList)
                built.Add((weapon.Address, BuildWeapon(weapon)));
            foreach (var character in CharacterList)
                built.Add((character.Address, BuildCharacter(character)));
            foreach (var monster in MonsterList)
                built.Add((monster.Address, BuildMonster(monster)));
            built.Add((ArrowAddress, BuildArrow()));
            built.Add((SpitAddress, BuildSpit()));
            built.Add((MagicBoltAddress, BuildMagicBolt()));
            built.Add((RockAddress, BuildRock()));
            built.Add((GroundDropAddress, BuildGroundDrop()));

            Register(built);
            AssetDatabase.SaveAssets();
            Debug.Log($"[EntityVisualPrefabBuilder] wrote {built.Count} visual prefabs under {OutputFolder}");
        }

        /// <summary>
        /// On a fresh Library the glTF and VRM importers can run before the shaders they need are imported and fail
        /// ("Value cannot be null. Parameter name: Shader"); importing them again fixes it.
        /// <see cref="FailedModelReimporter"/> does this after the import pass; this also repairs a Library that cached
        /// the failure earlier (Unity does not import an unchanged file again by itself).
        /// </summary>
        public static void ReimportFailedModels()
        {
            foreach (var path in ModelPaths())
            {
                if (AssetDatabase.LoadAssetAtPath<GameObject>(path) == null)
                {
                    Debug.Log($"[EntityVisualPrefabBuilder] reimporting {path}");
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                }
            }
        }

        /// <summary>The VRM avatars and glTF weapons the prefabs nest (the models the glTF and VRM importers make).</summary>
        public static IEnumerable<string> ModelPaths()
        {
            // A failed import has no asset type, so FindAssets does not list it: walk the files instead.
            return Directory.GetFiles(Avatars, "*.vrm").Concat(Directory.GetFiles(Weapons, "*.gltf")).Select(p => p.Replace('\\', '/'));
        }

        /// <summary>Whether <paramref name="assetPath"/> is one of <see cref="ModelPaths"/> (cheap: no file system access).</summary>
        public static bool IsModel(string assetPath) =>
            InFolder(assetPath, Avatars, ".vrm") || InFolder(assetPath, Weapons, ".gltf");

        private static bool InFolder(string assetPath, string folder, string extension) =>
            assetPath.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
            && assetPath.StartsWith(folder + "/", StringComparison.Ordinal)
            && assetPath.IndexOf('/', folder.Length + 1) < 0;

        private static string BuildWeapon(WeaponArt art)
        {
            var model = Load<GameObject>($"{Weapons}/{art.Model}");
            var root = new GameObject(Path.GetFileName(art.Address));
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(model, root.transform);
            instance.transform.localPosition = art.GripPosition;
            instance.transform.localRotation = Quaternion.Euler(art.GripEuler);
            instance.transform.localScale = Vector3.one * WeaponScale;
            return Save(root, $"{OutputFolder}/Weapons/{root.name}.prefab");
        }

        /// <summary>
        /// The M1 arrow (spec 3.2, <c>ProjectileDef.visualId</c> "arrow"): our own placeholder made of Unity primitives, so
        /// it needs no third-party art. The root sits on the projectile's ground point and faces +Z (the flight direction);
        /// the arrow flies at chest height, with its tip at the root.
        /// </summary>
        private static string BuildArrow()
        {
            var root = new GameObject(Path.GetFileName(ArrowAddress));
            var shaft = Primitive(PrimitiveType.Cylinder, "Shaft", root.transform, ArrowMaterial("ArrowShaft", new Color(0.55f, 0.38f, 0.2f)));
            shaft.localPosition = new Vector3(0f, ArrowHeightM, -0.35f);
            shaft.localRotation = Quaternion.Euler(90f, 0f, 0f); // the cylinder's axis is +Y; lay it along +Z
            shaft.localScale = new Vector3(0.025f, 0.35f, 0.025f); // 70 cm long
            var head = Primitive(PrimitiveType.Cube, "Head", root.transform, ArrowMaterial("ArrowHead", new Color(0.75f, 0.75f, 0.8f)));
            head.localPosition = new Vector3(0f, ArrowHeightM, -0.03f);
            head.localRotation = Quaternion.Euler(0f, 45f, 0f);
            head.localScale = new Vector3(0.05f, 0.02f, 0.05f);
            var fletching = Primitive(PrimitiveType.Cube, "Fletching", root.transform, ArrowMaterial("ArrowFletching", new Color(0.9f, 0.9f, 0.85f)));
            fletching.localPosition = new Vector3(0f, ArrowHeightM, -0.65f);
            fletching.localScale = new Vector3(0.002f, 0.06f, 0.1f);
            return Save(root, $"{OutputFolder}/Projectiles/{root.name}.prefab");
        }

        /// <summary>
        /// The alien's spit (<c>ProjectileDef</c> "alienSpit", backlog F): our own placeholder, a green blob of primitives with
        /// a short tail. The root sits on the projectile's ground point and faces +Z; the blob's front is at the root.
        /// </summary>
        private static string BuildSpit()
        {
            var root = new GameObject(Path.GetFileName(SpitAddress));
            var blob = Primitive(PrimitiveType.Sphere, "Blob", root.transform, ArrowMaterial("AlienSpit", new Color(0.45f, 0.85f, 0.2f)));
            blob.localPosition = new Vector3(0f, SpitHeightM, -0.1f);
            blob.localScale = new Vector3(0.2f, 0.2f, 0.2f);
            var tail = Primitive(PrimitiveType.Sphere, "Tail", root.transform, ArrowMaterial("AlienSpitTail", new Color(0.35f, 0.7f, 0.15f)));
            tail.localPosition = new Vector3(0f, SpitHeightM, -0.3f);
            tail.localScale = new Vector3(0.12f, 0.12f, 0.25f);
            return Save(root, $"{OutputFolder}/Projectiles/{root.name}.prefab");
        }

        /// <summary>
        /// The Mage's Magic Bolt (<c>ProjectileDef</c> "magicBolt", backlog E): our own PLACEHOLDER, a blue glowing blob with a
        /// tail, until its VFX exists. The root sits on the projectile's ground point and faces +Z; the bolt's front is at the root.
        /// </summary>
        private static string BuildMagicBolt()
        {
            var root = new GameObject(Path.GetFileName(MagicBoltAddress));
            var core = Primitive(PrimitiveType.Sphere, "Core", root.transform, ArrowMaterial("MagicBolt", new Color(0.35f, 0.55f, 1f)));
            core.localPosition = new Vector3(0f, ArrowHeightM, -0.1f);
            core.localScale = new Vector3(0.18f, 0.18f, 0.18f);
            var tail = Primitive(PrimitiveType.Sphere, "Tail", root.transform, ArrowMaterial("MagicBoltTail", new Color(0.6f, 0.75f, 1f)));
            tail.localPosition = new Vector3(0f, ArrowHeightM, -0.3f);
            tail.localScale = new Vector3(0.1f, 0.1f, 0.3f);
            return Save(root, $"{OutputFolder}/Projectiles/{root.name}.prefab");
        }

        /// <summary>
        /// The Cyclops boss's rock (<c>ProjectileDef</c> "cyclopsRock", WP10): our own placeholder, a grey lump of primitives.
        /// The root sits on the projectile's ground point and faces +Z; the rock's front is at the root.
        /// </summary>
        private static string BuildRock()
        {
            var root = new GameObject(Path.GetFileName(RockAddress));
            var rock = Primitive(PrimitiveType.Sphere, "Rock", root.transform, ArrowMaterial("CyclopsRock", new Color(0.45f, 0.42f, 0.4f)));
            rock.localPosition = new Vector3(0f, RockHeightM, -0.4f);
            rock.localRotation = Quaternion.Euler(20f, 35f, 10f);
            rock.localScale = new Vector3(0.8f, 0.6f, 0.7f);
            var chip = Primitive(PrimitiveType.Cube, "Chip", root.transform, ArrowMaterial("CyclopsRockChip", new Color(0.35f, 0.33f, 0.31f)));
            chip.localPosition = new Vector3(0.15f, RockHeightM + 0.2f, -0.45f);
            chip.localRotation = Quaternion.Euler(30f, 45f, 0f);
            chip.localScale = new Vector3(0.35f, 0.3f, 0.35f);
            return Save(root, $"{OutputFolder}/Projectiles/{root.name}.prefab");
        }

        /// <summary>
        /// A monster drop on the ground (backlog G): our own PLACEHOLDER, a brown sack with a gold glint and a thin light
        /// column so it reads from the 45-degree camera, until items have their own models. The root is the drop's ground
        /// point; the rows scale it per item.
        /// </summary>
        private static string BuildGroundDrop()
        {
            var root = new GameObject(Path.GetFileName(GroundDropAddress));
            var sack = Primitive(PrimitiveType.Sphere, "Sack", root.transform, ArrowMaterial("GroundDropSack", new Color(0.45f, 0.3f, 0.15f)));
            sack.localPosition = new Vector3(0f, 0.18f, 0f);
            sack.localScale = new Vector3(0.45f, 0.36f, 0.45f);
            var glint = Primitive(PrimitiveType.Cube, "Glint", root.transform, ArrowMaterial("GroundDropGlint", new Color(1f, 0.8f, 0.25f)));
            glint.localPosition = new Vector3(0f, 0.42f, 0f);
            glint.localRotation = Quaternion.Euler(45f, 45f, 0f);
            glint.localScale = new Vector3(0.12f, 0.12f, 0.12f);
            var beam = Primitive(PrimitiveType.Cylinder, "Beam", root.transform, ArrowMaterial("GroundDropBeam", new Color(1f, 0.9f, 0.5f)));
            beam.localPosition = new Vector3(0f, 1.2f, 0f);
            beam.localScale = new Vector3(0.04f, 0.8f, 0.04f);
            return Save(root, $"{OutputFolder}/Items/{root.name}.prefab");
        }

        private static Transform Primitive(PrimitiveType type, string name, Transform parent, Material material)
        {
            var part = GameObject.CreatePrimitive(type);
            part.name = name;
            UnityEngine.Object.DestroyImmediate(part.GetComponent<Collider>());
            part.GetComponent<Renderer>().sharedMaterial = material;
            part.transform.SetParent(parent, false);
            return part.transform;
        }

        private static Material ArrowMaterial(string name, Color color)
        {
            string path = $"{OutputFolder}/Materials/{name}.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Lit")
                    ?? throw new InvalidOperationException("URP Lit shader not found");
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, path);
            }
            material.SetColor("_BaseColor", color);
            material.SetFloat("_Smoothness", 0.2f);
            EditorUtility.SetDirty(material);
            return material;
        }

        private static string BuildCharacter(CharacterArt art)
        {
            string name = Path.GetFileName(art.Address);
            var set = HumanoidSet(name, art);
            var model = Load<GameObject>($"{Avatars}/{art.Avatar}");
            var root = new GameObject(name);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(model, root.transform);
            var animator = instance.GetComponent<Animator>();
            if (animator == null || !animator.isHuman)
                throw new InvalidOperationException($"{art.Avatar} did not import as a humanoid");
            root.AddComponent<EntityVisualRig>().Configure(animator, set);
            return Save(root, $"{OutputFolder}/Characters/{name}.prefab");
        }

        private static string BuildMonster(MonsterArt art)
        {
            string name = Path.GetFileName(art.Address);
            string modelPath = $"{Monsters}/{art.Model}";
            var set = MonsterSet(name, modelPath, art);
            var model = Load<GameObject>(modelPath);
            var root = new GameObject(name);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(model, root.transform);
            instance.transform.localRotation = Quaternion.Euler(0f, art.YawDegrees, 0f);
            // The FBX materials lost their texture link: give every renderer a URP material with the pack's texture.
            var material = MonsterMaterial(name);
            foreach (var renderer in instance.GetComponentsInChildren<Renderer>())
                renderer.sharedMaterials = Enumerable.Repeat(material, renderer.sharedMaterials.Length).ToArray();
            var animator = instance.GetComponent<Animator>();
            if (animator == null)
                animator = instance.AddComponent<Animator>();
            root.AddComponent<EntityVisualRig>().Configure(animator, set);
            return Save(root, $"{OutputFolder}/Monsters/{name}.prefab");
        }

        private static Material MonsterMaterial(string name)
        {
            string path = $"{OutputFolder}/Materials/{name}.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Lit")
                    ?? throw new InvalidOperationException("URP Lit shader not found");
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, path);
            }
            material.SetTexture("_BaseMap", Load<Texture2D>($"{Monsters}/Textures/{name}_Texture.png"));
            material.SetColor("_BaseColor", Color.white);
            material.SetFloat("_Smoothness", 0.2f);
            EditorUtility.SetDirty(material);
            return material;
        }

        private static AnimationSet HumanoidSet(string name, CharacterArt art)
        {
            var set = LoadOrCreate($"{OutputFolder}/AnimationSets/{name}.asset");
            set.idle = Clip(Ual1, "Idle_Loop");
            set.walk = Clip(Ual1, "Walk_Loop");
            set.run = Clip(Ual1, "Jog_Fwd_Loop");
            set.walkReferenceSpeedCmPerSec = ReferenceSpeed(set.walk, DefaultWalkCmPerSec);
            set.runReferenceSpeedCmPerSec = Math.Max(set.walkReferenceSpeedCmPerSec + 1f, ReferenceSpeed(set.run, DefaultRunCmPerSec));
            set.defaultAction = new ActionClipEntry
            {
                clip = Clip(Ual1, art.Attack),
                startupEnd = 0.35f,
                activeEnd = 0.55f,
                channelLoop = art.ChannelLoop != null ? Clip(Ual1, art.ChannelLoop) : null,
            };
            // One entry per kit action (backlog E); phase timings come from ActionDef, the marks only place the hit in the take.
            set.actions = art.Actions
                .Select(a => new ActionClipEntry
                {
                    actionId = a.ActionId,
                    clip = Clip(a.Library, a.Take),
                    startupEnd = 0.35f,
                    activeEnd = 0.55f,
                    channelLoop = a.ChannelLoop != null ? Clip(a.Library, a.ChannelLoop) : null,
                })
                .ToList();
            set.flinch = Clip(Ual1, "Hit_Chest");
            set.stagger = Clip(Ual2, "Hit_Knockback");
            set.death = Clip(Ual1, "Death01");
            EditorUtility.SetDirty(set);
            return set;
        }

        private static AnimationSet MonsterSet(string name, string modelPath, MonsterArt art)
        {
            var set = LoadOrCreate($"{OutputFolder}/AnimationSets/{name}.asset");
            set.idle = Clip(modelPath, "Idle");
            set.walk = Clip(modelPath, "Walk");
            set.run = null;
            set.walkReferenceSpeedCmPerSec = ReferenceSpeed(set.walk, DefaultWalkCmPerSec);
            set.runReferenceSpeedCmPerSec = set.walkReferenceSpeedCmPerSec;
            set.defaultAction = new ActionClipEntry { clip = Clip(modelPath, "Bite_Front"), startupEnd = 0.4f, activeEnd = 0.6f };
            // Phase timings come from ActionDef; the marks only say where the take's wind-up and hit are.
            set.actions = art.Actions
                .Select(a => new ActionClipEntry { actionId = a.ActionId, clip = Clip(modelPath, a.Take), startupEnd = 0.4f, activeEnd = 0.6f })
                .ToList();
            set.flinch = Clip(modelPath, "HitRecieve");
            set.stagger = Clip(modelPath, "HitRecieve");
            set.death = Clip(modelPath, "Death");
            EditorUtility.SetDirty(set);
            return set;
        }

        /// <summary>The clip's own ground speed (root motion), else the fallback.</summary>
        private static float ReferenceSpeed(AnimationClip? clip, float fallbackCmPerSec)
        {
            if (clip == null)
                return fallbackCmPerSec;
            var velocity = clip.averageSpeed;
            float cmPerSec = new Vector2(velocity.x, velocity.z).magnitude * 100f;
            return cmPerSec > 10f ? Mathf.Round(cmPerSec) : fallbackCmPerSec;
        }

        /// <summary>A take of a model file; Unity names clips after the take ("Rig|Idle_Loop").</summary>
        public static AnimationClip Clip(string modelPath, string take)
        {
            var clips = AssetDatabase.LoadAllAssetsAtPath(modelPath).OfType<AnimationClip>()
                .Where(c => !c.name.StartsWith("__preview__", StringComparison.Ordinal)).ToList();
            var clip = clips.FirstOrDefault(c => c.name == take || c.name.EndsWith("|" + take, StringComparison.Ordinal));
            if (clip == null)
                throw new InvalidOperationException($"{modelPath} has no clip '{take}'; it has: {string.Join(", ", clips.Select(c => c.name))}");
            return clip;
        }

        private static AnimationSet LoadOrCreate(string path)
        {
            var set = AssetDatabase.LoadAssetAtPath<AnimationSet>(path);
            if (set != null)
                return set;
            set = ScriptableObject.CreateInstance<AnimationSet>();
            AssetDatabase.CreateAsset(set, path);
            return set;
        }

        private static T Load<T>(string path) where T : UnityEngine.Object
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset == null)
                throw new InvalidOperationException($"missing or failed import: {path}");
            return asset;
        }

        private static string Save(GameObject root, string path)
        {
            PrefabUtility.SaveAsPrefabAsset(root, path, out bool ok);
            UnityEngine.Object.DestroyImmediate(root);
            if (!ok)
                throw new InvalidOperationException($"could not save {path}");
            return path;
        }

        private static void Register(List<(string Address, string Path)> built)
        {
            var settings = AddressableAssetSettingsDefaultObject.GetSettings(true);
            var group = settings.FindGroup(GroupName)
                ?? settings.CreateGroup(GroupName, false, false, true, null, typeof(BundledAssetGroupSchema), typeof(ContentUpdateGroupSchema));
            foreach (var (address, path) in built)
            {
                var entry = settings.CreateOrMoveEntry(AssetDatabase.AssetPathToGUID(path), group, false, false);
                entry.address = address;
            }
            settings.SetDirty(AddressableAssetSettings.ModificationEvent.BatchModification, null, true, true);
        }
    }

    /// <summary>
    /// The UniGLTF and UniVRM importers look their shaders up with <c>Shader.Find</c> ("Universal Render Pipeline/Lit",
    /// "VRM10/Universal Render Pipeline/MToon10") and declare no import dependency on them. In the first import of a
    /// fresh Library a model can come before its shader and fail ("Value cannot be null. Parameter name: Shader"); Unity
    /// keeps that failed result, so the character and weapon prefabs lose their nested model (no humanoid bones) until the
    /// file is imported again. This imports such a model again right after its import pass, when the shaders are in.
    /// Each model is retried once per editor session, so a model that is broken for another reason (for example a Git
    /// LFS pointer instead of the file) does not loop.
    /// </summary>
    internal sealed class FailedModelReimporter : AssetPostprocessor
    {
        private static readonly HashSet<string> Retried = new HashSet<string>();

        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] movedTo, string[] movedFrom)
        {
            foreach (var path in imported)
            {
                if (!EntityVisualPrefabBuilder.IsModel(path) || AssetDatabase.LoadAssetAtPath<GameObject>(path) != null)
                    continue;
                if (!Retried.Add(path))
                {
                    Debug.LogWarning($"[FailedModelReimporter] {path} failed to import again; see the importer error above");
                    continue;
                }
                Debug.Log($"[FailedModelReimporter] {path} failed to import (its shader was not imported yet); importing it again");
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            }
        }
    }
}
