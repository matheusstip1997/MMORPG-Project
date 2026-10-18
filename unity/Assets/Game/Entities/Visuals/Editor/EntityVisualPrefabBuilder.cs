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
    /// prefab per weapon (the KayKit model with its grip offset, so it sits in a humanoid right or left hand), and an
    /// animation set per body (Quaternius clips). Our own art replaces these by new prefabs at the same addresses.
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
            public CharacterArt(string address, string avatar, string attack, string? channelLoop)
            {
                Address = address;
                Avatar = avatar;
                Attack = attack;
                ChannelLoop = channelLoop;
            }

            public string Address { get; }
            public string Avatar { get; }

            /// <summary>Default action clip (Universal Animation Library take).</summary>
            public string Attack { get; }
            public string? ChannelLoop { get; }
        }

        private sealed class MonsterArt
        {
            public MonsterArt(string address, string model, float yawDegrees)
            {
                Address = address;
                Model = model;
                YawDegrees = yawDegrees;
            }

            public string Address { get; }
            public string Model { get; }

            /// <summary>Turn so the model faces +Z like the entity root.</summary>
            public float YawDegrees { get; }
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
            new CharacterArt("Visuals/Characters/Warrior", "AvatarSample_D.vrm", "Sword_Attack", null),
            new CharacterArt("Visuals/Characters/Archer", "AvatarSample_E.vrm", "Pistol_Shoot", null),
            new CharacterArt("Visuals/Characters/Mage", "AvatarSample_F.vrm", "Spell_Simple_Shoot", "Spell_Simple_Idle_Loop"),
            new CharacterArt("Visuals/Characters/Assist", "AvatarSample_G.vrm", "Spell_Simple_Shoot", "Spell_Simple_Idle_Loop"),
        };

        private static readonly MonsterArt[] MonsterList =
        {
            new MonsterArt("Visuals/Monsters/Demon", "Demon.fbx", 0f),
            new MonsterArt("Visuals/Monsters/Alien", "Alien.fbx", 0f),
            new MonsterArt("Visuals/Monsters/Cyclops", "Cyclops.fbx", 0f),
        };

        [MenuItem("MMORPG/Visuals/Rebuild Visual Prefabs")]
        public static void Build()
        {
            ReimportFailedModels();
            Directory.CreateDirectory(OutputFolder + "/Characters");
            Directory.CreateDirectory(OutputFolder + "/Monsters");
            Directory.CreateDirectory(OutputFolder + "/Weapons");
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

            Register(built);
            AssetDatabase.SaveAssets();
            Debug.Log($"[EntityVisualPrefabBuilder] wrote {built.Count} visual prefabs under {OutputFolder}");
        }

        /// <summary>
        /// On a fresh Library the glTF and VRM importers can run before the shaders they need are imported and fail
        /// ("Value cannot be null. Parameter name: Shader"); importing them again fixes it.
        /// </summary>
        public static void ReimportFailedModels()
        {
            // A failed import has no asset type, so FindAssets does not list it: walk the files instead.
            var models = Directory.GetFiles(Avatars, "*.vrm").Concat(Directory.GetFiles(Weapons, "*.gltf")).Select(p => p.Replace('\\', '/'));
            foreach (var path in models)
            {
                if (AssetDatabase.LoadAssetAtPath<GameObject>(path) == null)
                {
                    Debug.Log($"[EntityVisualPrefabBuilder] reimporting {path}");
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                }
            }
        }

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
            var set = MonsterSet(name, modelPath);
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
            // TODO(D2): entries per ActionDef id of each class's starting kit once the action tables exist.
            set.actions = new List<ActionClipEntry>();
            set.flinch = Clip(Ual1, "Hit_Chest");
            set.stagger = Clip(Ual2, "Hit_Knockback");
            set.death = Clip(Ual1, "Death01");
            EditorUtility.SetDirty(set);
            return set;
        }

        private static AnimationSet MonsterSet(string name, string modelPath)
        {
            var set = LoadOrCreate($"{OutputFolder}/AnimationSets/{name}.asset");
            set.idle = Clip(modelPath, "Idle");
            set.walk = Clip(modelPath, "Walk");
            set.run = null;
            set.walkReferenceSpeedCmPerSec = ReferenceSpeed(set.walk, DefaultWalkCmPerSec);
            set.runReferenceSpeedCmPerSec = set.walkReferenceSpeedCmPerSec;
            set.defaultAction = new ActionClipEntry { clip = Clip(modelPath, "Bite_Front"), startupEnd = 0.4f, activeEnd = 0.6f };
            set.actions = new List<ActionClipEntry>();
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
}
