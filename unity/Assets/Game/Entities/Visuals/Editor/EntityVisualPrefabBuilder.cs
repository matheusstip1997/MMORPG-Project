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
using UnityEngine.Animations;
using UnityEngine.Playables;

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

        // The humanoid locomotion and reaction takes (Universal Animation Library). The Standard editions have forward loops
        // only: the backward slots replay the forward ones in reverse, and the leg turn covers the sides (art data).
        private const string IdleTake = "Idle_Loop";
        private const string WalkTake = "Walk_Loop";
        private const string JogTake = "Jog_Fwd_Loop";
        private const string FlinchTake = "Hit_Chest";
        private const string StaggerTake = "Hit_Knockback";
        private const string DeathTake = "Death01";

        // Where the humanoid action takes are cut into phases by default: the wind-up, the hit, the follow-through.
        private const float DefaultStartupEnd = 0.35f;
        private const float DefaultActiveEnd = 0.55f;

        // PLACEHOLDER dodge of every class (ActionDef "dodge": 50 ms startup, 250 ms dash, 200 ms recovery): the in-place
        // roll, cut so the tumble fills the dash. It rolls along the facing whatever the dash direction (the packs have no
        // backward or side roll). Without an entry the dodge played the set's default action (the attack swing).
        private const string DodgeTake = "Roll";
        private const float DodgeStartupEnd = 0.1f;
        private const float DodgeActiveEnd = 0.7f;

        // The placeholder leg turn of the humanoid sets: the full 90 degrees, so a pure strafe walks along the legs with no
        // side loop; the legs keep walking forward until the movement is 15 degrees past the facing's side, and turn at
        // 540 degrees per second (a flip from one side to the other in a third of a second).
        private const float LegTurnMaxDeg = 90f;
        private const float LegTurnHysteresisDeg = 15f;
        private const float LegTurnDegPerSec = 540f;

        // PLACEHOLDER ground contact of the humanoid sets: the Standard jog's authored flight lifts the lowest sole up to
        // 22 cm on the VRoid avatars (in place, root Y baked), which reads as floating (fix/floating-2). While locomotion
        // shows, the hips come down until the lowest sole is within 4 cm of the ground, by at most 30 cm. A lower tolerance
        // pulls the swinging foot onto the ground during the jog's flight, where it skates (archer: 230 cm/s at 2 cm, 62 at 4).
        private const float GroundContactToleranceCm = 4f;
        private const float GroundContactMaxCm = 30f;

        // The Cute Monsters takes the monster sets use besides their attacks.
        private const string MonsterIdleTake = "Idle";
        private const string MonsterWalkTake = "Walk";
        private const string MonsterAttackTake = "Bite_Front";
        private const string MonsterHitTake = "HitRecieve";
        private const string MonsterDeathTake = "Death";

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

        /// <summary>The Mage's spells (<c>EntityVisualDef</c> rows "fireSpark", "iceShard", "windCut"); they fly at chest height.</summary>
        private const string FireSparkAddress = "Visuals/Projectiles/FireSpark";
        private const string IceShardAddress = "Visuals/Projectiles/IceShard";
        private const string WindCutAddress = "Visuals/Projectiles/WindCut";

        /// <summary>Height of the flat action effects above the ground, so they do not flicker with it.</summary>
        private const float EffectHeightM = 0.03f;

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

        /// <summary>The clip of one action: a take of the Universal Animation Library (part 1 or 2), and its effect.</summary>
        private sealed class ActionArt
        {
            public ActionArt(string actionId, string library, string take, string? channelLoop = null, string? vfxId = null,
                float vfxLifetimeMs = 600f, float startupEnd = DefaultStartupEnd, float activeEnd = DefaultActiveEnd)
            {
                ActionId = actionId;
                Library = library;
                Take = take;
                ChannelLoop = channelLoop;
                VfxId = vfxId;
                VfxLifetimeMs = vfxLifetimeMs;
                StartupEnd = startupEnd;
                ActiveEnd = activeEnd;
            }

            /// <summary>The <c>EntityVisualDef</c> id of the effect shown when Active starts (ActionVfx), or null.</summary>
            public string? VfxId { get; }

            /// <summary>How long the effect stays unless the action has a ground area (art data).</summary>
            public float VfxLifetimeMs { get; }

            public string ActionId { get; }

            /// <summary><see cref="Ual1"/> or <see cref="Ual2"/>.</summary>
            public string Library { get; }
            public string Take { get; }

            /// <summary>A loop of the same library for the Channel phase, or null.</summary>
            public string? ChannelLoop { get; }

            /// <summary>Where Startup ends and Active begins in the take, as a fraction of it (<see cref="ActionClipEntry"/>).</summary>
            public float StartupEnd { get; }

            /// <summary>Where Active ends and Recovery begins in the take, as a fraction of it.</summary>
            public float ActiveEnd { get; }
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

        private const string SpellLoop = "Spell_Simple_Idle_Loop";

        private static readonly CharacterArt[] CharacterList =
        {
            // PLACEHOLDER takes for the M1 kits (ActionBindings rows); the user's animations replace them.
            new CharacterArt("Visuals/Characters/Warrior", "AvatarSample_D.vrm", "Sword_Attack", null,
                // The attack chain (R-3.1-10): one take per hit, the finisher on the widest swing.
                new ActionArt("attack.warrior", Ual1, "Sword_Attack"),
                new ActionArt("attack.warrior.2", Ual2, "Sword_Regular_A"),
                new ActionArt("attack.warrior.3", Ual2, "Sword_Regular_B"),
                new ActionArt("attack.warrior.4", Ual2, "Sword_Regular_C"),
                new ActionArt("dodge", Ual1, DodgeTake, startupEnd: DodgeStartupEnd, activeEnd: DodgeActiveEnd),
                new ActionArt("skill.warrior.slash", Ual2, "Sword_Regular_A"),
                new ActionArt("skill.warrior.headCut", Ual2, "Sword_Regular_B"),
                new ActionArt("skill.warrior.piercingAttack", Ual2, "Sword_Regular_C"),
                new ActionArt("skill.warrior.jumpStrike", Ual2, "Sword_Regular_Combo"),
                new ActionArt("skill.warrior.staminaBuff", Ual2, "Sword_Block")),
            new CharacterArt("Visuals/Characters/Archer", "AvatarSample_E.vrm", "Pistol_Shoot", null,
                new ActionArt("attack.archer", Ual1, "Pistol_Shoot"),
                new ActionArt("attack.archer.2", Ual1, "Pistol_Shoot"),
                new ActionArt("attack.archer.3", Ual1, "Pistol_Shoot"),
                new ActionArt("attack.archer.4", Ual1, "Pistol_Shoot"),
                new ActionArt("dodge", Ual1, DodgeTake, startupEnd: DodgeStartupEnd, activeEnd: DodgeActiveEnd),
                new ActionArt("skill.archer.piercingShot", Ual1, "Pistol_Shoot"),
                new ActionArt("skill.archer.multiShot", Ual1, "Pistol_Shoot"),
                new ActionArt("skill.archer.heavyShot", Ual1, "Pistol_Shoot", "Pistol_Aim_Neutral"),
                new ActionArt("skill.archer.backStep", Ual1, "Roll_RM"),
                new ActionArt("skill.archer.dash", Ual1, "Roll_RM")),
            // Channel spells hold the spell idle loop during their Channel phase; the Assist's circles name their effect (ActionVfx).
            new CharacterArt("Visuals/Characters/Mage", "AvatarSample_F.vrm", "Spell_Simple_Shoot", "Spell_Simple_Idle_Loop",
                new ActionArt("attack.mage", Ual1, "Spell_Simple_Shoot"),
                new ActionArt("attack.mage.2", Ual1, "Spell_Simple_Shoot"),
                new ActionArt("attack.mage.3", Ual1, "Spell_Simple_Shoot"),
                new ActionArt("attack.mage.4", Ual1, "Spell_Simple_Shoot"),
                new ActionArt("dodge", Ual1, DodgeTake, startupEnd: DodgeStartupEnd, activeEnd: DodgeActiveEnd),
                new ActionArt("skill.mage.fireSpark", Ual1, "Spell_Simple_Shoot", SpellLoop),
                new ActionArt("skill.mage.iceShard", Ual1, "Spell_Simple_Shoot", SpellLoop),
                new ActionArt("skill.mage.windCut", Ual1, "Spell_Simple_Shoot", SpellLoop),
                // Its fire is the ground area's own view (ActionEffectDef visualId vfx.flamethrower), not an action effect.
                new ActionArt("skill.mage.flamethrower", Ual1, "Spell_Simple_Shoot", SpellLoop),
                new ActionArt("skill.mage.magicBarrier", Ual1, "Spell_Simple_Enter")),
            new CharacterArt("Visuals/Characters/Assist", "AvatarSample_G.vrm", "Spell_Simple_Shoot", "Spell_Simple_Idle_Loop",
                new ActionArt("attack.assist", Ual1, "Sword_Attack"),
                new ActionArt("attack.assist.2", Ual2, "Sword_Regular_A"),
                new ActionArt("attack.assist.3", Ual2, "Sword_Regular_B"),
                new ActionArt("attack.assist.4", Ual2, "Sword_Regular_C"),
                new ActionArt("dodge", Ual1, DodgeTake, startupEnd: DodgeStartupEnd, activeEnd: DodgeActiveEnd),
                new ActionArt("skill.assist.holyStrike", Ual1, "Spell_Simple_Shoot", SpellLoop),
                new ActionArt("skill.assist.healWave", Ual1, "Spell_Simple_Exit", SpellLoop, "vfx.healWave", 800f),
                new ActionArt("skill.assist.flash", Ual1, "Spell_Simple_Enter", SpellLoop, "vfx.flash", 300f),
                new ActionArt("skill.assist.simpleCleanse", Ual1, "Spell_Simple_Exit", SpellLoop, "vfx.simpleCleanse", 800f),
                new ActionArt("skill.assist.lifeRegeneration", Ual1, "Spell_Simple_Enter", SpellLoop, "vfx.lifeRegeneration", 1000f)),
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
            foreach (var library in HumanoidLibraries)
                BakeRootHeight(library);
            Directory.CreateDirectory(OutputFolder + "/Characters");
            Directory.CreateDirectory(OutputFolder + "/Monsters");
            Directory.CreateDirectory(OutputFolder + "/Weapons");
            Directory.CreateDirectory(OutputFolder + "/Projectiles");
            Directory.CreateDirectory(OutputFolder + "/Items");
            Directory.CreateDirectory(OutputFolder + "/Effects");
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
            built.Add((FireSparkAddress, BuildFireSpark()));
            built.Add((IceShardAddress, BuildIceShard()));
            built.Add((WindCutAddress, BuildWindCut()));
            built.Add(("Visuals/Effects/Flamethrower", BuildFlamethrower()));
            built.Add(("Visuals/Effects/HealWave", BuildRingEffect("HealWave", 5f, new Color(0.45f, 1f, 0.55f), centerGlow: false)));
            built.Add(("Visuals/Effects/Flash", BuildRingEffect("Flash", 3f, new Color(1f, 0.95f, 0.6f), centerGlow: true)));
            built.Add(("Visuals/Effects/SimpleCleanse", BuildRingEffect("SimpleCleanse", 4f, new Color(0.6f, 0.9f, 1f), centerGlow: false)));
            built.Add(("Visuals/Effects/LifeRegeneration", BuildRingEffect("LifeRegeneration", 6f, new Color(0.3f, 0.85f, 0.35f), centerGlow: false)));

            Register(built);
            AssetDatabase.SaveAssets();
            Debug.Log($"[EntityVisualPrefabBuilder] wrote {built.Count} visual prefabs under {OutputFolder}");
        }

        /// <summary>The humanoid animation libraries the character animation sets take their clips from.</summary>
        public static readonly string[] HumanoidLibraries = { Ual1, Ual2 };

        /// <summary>
        /// Sets "Bake Into Pose" for root Y (<see cref="ModelImporterClipAnimation.lockRootHeightY"/>) on every clip of the
        /// humanoid <paramref name="modelPath"/>. The clips walk in place and keep the body's height curve as root motion;
        /// <see cref="VisualAnimator"/> plays with root motion off (the entity root alone places the visual), which would drop
        /// that curve: the hips lose their bob and the stance foot hangs above the ground (first play session P1, "the
        /// character floats"). Idempotent: a model already baked is not touched. True when it changed the import settings.
        /// </summary>
        public static bool BakeRootHeight(string modelPath)
        {
            var importer = AssetImporter.GetAtPath(modelPath) as ModelImporter
                ?? throw new InvalidOperationException($"{modelPath} is not a model");
            bool explicitClips = importer.clipAnimations.Length > 0;
            var clips = explicitClips ? importer.clipAnimations : importer.defaultClipAnimations;
            bool changed = !explicitClips;
            foreach (var clip in clips)
            {
                if (clip.lockRootHeightY)
                    continue;
                clip.lockRootHeightY = true;
                changed = true;
            }
            if (!changed)
                return false;
            importer.clipAnimations = clips;
            importer.SaveAndReimport();
            Debug.Log($"[EntityVisualPrefabBuilder] baked root Y into the pose of {clips.Length} clips of {modelPath}");
            return true;
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

        /// <summary>
        /// The Mage's Fire Spark (<c>ProjectileDef</c> "fireSpark", backlog E): our own PLACEHOLDER, an orange blob with a red
        /// tail. The root sits on the projectile's ground point and faces +Z; the bolt's front is at the root.
        /// </summary>
        private static string BuildFireSpark()
        {
            var root = new GameObject(Path.GetFileName(FireSparkAddress));
            var core = Primitive(PrimitiveType.Sphere, "Core", root.transform, ArrowMaterial("FireSpark", new Color(1f, 0.6f, 0.15f)));
            core.localPosition = new Vector3(0f, ArrowHeightM, -0.08f);
            core.localScale = new Vector3(0.16f, 0.16f, 0.16f);
            var tail = Primitive(PrimitiveType.Sphere, "Tail", root.transform, ArrowMaterial("FireSparkTail", new Color(0.9f, 0.2f, 0.05f)));
            tail.localPosition = new Vector3(0f, ArrowHeightM, -0.25f);
            tail.localScale = new Vector3(0.09f, 0.09f, 0.25f);
            return Save(root, $"{OutputFolder}/Projectiles/{root.name}.prefab");
        }

        /// <summary>
        /// The Mage's Ice Shard (<c>ProjectileDef</c> "iceShard", backlog E): our own PLACEHOLDER, a pale-blue diamond spike.
        /// The root sits on the projectile's ground point and faces +Z; the tip is at the root.
        /// </summary>
        private static string BuildIceShard()
        {
            var root = new GameObject(Path.GetFileName(IceShardAddress));
            var shard = Primitive(PrimitiveType.Cube, "Shard", root.transform, ArrowMaterial("IceShard", new Color(0.7f, 0.9f, 1f)));
            shard.localPosition = new Vector3(0f, ArrowHeightM, -0.25f);
            shard.localRotation = Quaternion.Euler(0f, 0f, 45f);
            shard.localScale = new Vector3(0.1f, 0.1f, 0.5f);
            return Save(root, $"{OutputFolder}/Projectiles/{root.name}.prefab");
        }

        /// <summary>
        /// The Mage's Wind Cut (<c>ProjectileDef</c> "windCut", backlog E): our own PLACEHOLDER, a pale-green crescent of three
        /// thin blades as wide as its hit circle. The root sits on the projectile's ground point and faces +Z.
        /// </summary>
        private static string BuildWindCut()
        {
            var root = new GameObject(Path.GetFileName(WindCutAddress));
            var material = ArrowMaterial("WindCut", new Color(0.7f, 1f, 0.75f));
            for (int i = -1; i <= 1; i++)
            {
                var blade = Primitive(PrimitiveType.Cube, $"Blade{i + 1}", root.transform, material);
                blade.localPosition = new Vector3(i * 0.3f, ArrowHeightM, -0.05f - Math.Abs(i) * 0.12f);
                blade.localRotation = Quaternion.Euler(0f, i * 35f, 0f);
                blade.localScale = new Vector3(0.32f, 0.02f, 0.06f);
            }
            return Save(root, $"{OutputFolder}/Projectiles/{root.name}.prefab");
        }

        /// <summary>
        /// The Mage's Flamethrower area (<c>EntityVisualDef</c> "vfx.flamethrower", the ground area's <c>visualId</c>): our own
        /// PLACEHOLDER, a fan of flat flame tongues over its hit shape (a cone of 3.5 m, 30 degrees each side). The root is the
        /// area's origin (the caster's ground point) facing +Z, as GroundAreaViews places it.
        /// </summary>
        private static string BuildFlamethrower()
        {
            var root = new GameObject("Flamethrower");
            var outer = ArrowMaterial("FlamethrowerOuter", new Color(1f, 0.35f, 0.05f));
            var inner = ArrowMaterial("FlamethrowerInner", new Color(1f, 0.8f, 0.2f));
            const float rangeM = 3.5f;
            for (int i = -2; i <= 2; i++)
            {
                float angle = i * 15f; // -30 to +30 degrees
                var tongue = Primitive(PrimitiveType.Cube, $"Tongue{i + 2}", root.transform, i == 0 ? inner : outer);
                var direction = Quaternion.Euler(0f, angle, 0f);
                tongue.localRotation = direction;
                tongue.localPosition = direction * new Vector3(0f, EffectHeightM + 0.3f, rangeM / 2f);
                tongue.localScale = new Vector3(0.35f, 0.6f, rangeM);
            }
            return Save(root, $"{OutputFolder}/Effects/{root.name}.prefab");
        }

        /// <summary>
        /// A circle effect of the Assist's kit (backlog E): our own PLACEHOLDER, a ring of flat segments on the ground at the
        /// skill's radius (its hit circle), with an optional glow at the center. The root is the caster's ground point.
        /// </summary>
        private static string BuildRingEffect(string name, float radiusM, Color color, bool centerGlow)
        {
            const int segments = 32;
            var root = new GameObject(name);
            var material = ArrowMaterial(name, color);
            float segmentLength = 2f * Mathf.PI * radiusM / segments * 1.05f;
            for (int i = 0; i < segments; i++)
            {
                float angle = 360f * i / segments;
                var direction = Quaternion.Euler(0f, angle, 0f);
                var segment = Primitive(PrimitiveType.Cube, $"Ring{i}", root.transform, material);
                segment.localRotation = direction;
                segment.localPosition = direction * new Vector3(0f, EffectHeightM, radiusM);
                segment.localScale = new Vector3(segmentLength, 0.02f, 0.15f);
            }
            if (centerGlow)
            {
                var glow = Primitive(PrimitiveType.Sphere, "Glow", root.transform, material);
                glow.localPosition = new Vector3(0f, 1.2f, 0f);
                glow.localScale = Vector3.one * 0.8f;
            }
            return Save(root, $"{OutputFolder}/Effects/{root.name}.prefab");
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
            // The Quaternius loops walk in place (no root motion to read), and retargeting scales their stride to each
            // avatar's legs: measure the speed at which the feet stay planted on this avatar, for every direction.
            set.walkReferenceSpeedCmPerSec = StanceSpeed(model, set.walk!, DefaultWalkCmPerSec);
            set.runReferenceSpeedCmPerSec = Math.Max(set.walkReferenceSpeedCmPerSec + 1f, StanceSpeed(model, set.run!, DefaultRunCmPerSec));
            for (int direction = 1; direction < LocomotionProfile.Directions; direction++)
            {
                var walkSlot = set.Slot(walk: true, direction)!;
                var runSlot = set.Slot(walk: false, direction)!;
                walkSlot.referenceSpeedCmPerSec = walkSlot.HasClip
                    ? StanceSpeed(model, walkSlot.clip!, DefaultWalkCmPerSec, (LocomotionDirection)direction, walkSlot.reverse)
                    : 0f;
                runSlot.referenceSpeedCmPerSec = runSlot.HasClip
                    ? Math.Max(walkSlot.referenceSpeedCmPerSec + 1f,
                        StanceSpeed(model, runSlot.clip!, DefaultRunCmPerSec, (LocomotionDirection)direction, runSlot.reverse))
                    : 0f;
            }
            EditorUtility.SetDirty(set);
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
            set.idle = Clip(Ual1, IdleTake);
            set.walk = Clip(Ual1, WalkTake);
            set.run = Clip(Ual1, JogTake);
            // PLACEHOLDER directions (no backward or side loops in the Standard packs): backward replays the forward loops
            // in reverse, the sides are the leg turn. A pack with real loops fills the slots and sets legTurnMaxDeg to 0.
            set.walkBackward = new LocomotionClip { clip = Clip(Ual1, WalkTake), reverse = true };
            set.runBackward = new LocomotionClip { clip = Clip(Ual1, JogTake), reverse = true };
            set.walkLeft = new LocomotionClip();
            set.walkRight = new LocomotionClip();
            set.runLeft = new LocomotionClip();
            set.runRight = new LocomotionClip();
            set.legTurnMaxDeg = LegTurnMaxDeg;
            set.legTurnHysteresisDeg = LegTurnHysteresisDeg;
            set.legTurnDegPerSec = LegTurnDegPerSec;
            set.groundContact = true;
            set.groundContactToleranceCm = GroundContactToleranceCm;
            set.groundContactMaxCm = GroundContactMaxCm;
            // The reference speeds are measured on the avatar (BuildCharacter, StanceSpeed).
            set.defaultAction = new ActionClipEntry
            {
                clip = Clip(Ual1, art.Attack),
                startupEnd = DefaultStartupEnd,
                activeEnd = DefaultActiveEnd,
                channelLoop = art.ChannelLoop != null ? Clip(Ual1, art.ChannelLoop) : null,
            };
            // One entry per kit action (backlog E); phase timings come from ActionDef, the marks only place the hit in the take.
            set.actions = art.Actions
                .Select(a => new ActionClipEntry
                {
                    actionId = a.ActionId,
                    clip = Clip(a.Library, a.Take),
                    startupEnd = a.StartupEnd,
                    activeEnd = a.ActiveEnd,
                    channelLoop = a.ChannelLoop != null ? Clip(a.Library, a.ChannelLoop) : null,
                    vfxId = a.VfxId ?? string.Empty,
                    vfxLifetimeMs = a.VfxLifetimeMs,
                })
                .ToList();
            set.flinch = Clip(Ual1, FlinchTake);
            set.stagger = Clip(Ual2, StaggerTake);
            set.death = Clip(Ual1, DeathTake);
            EditorUtility.SetDirty(set);
            return set;
        }

        /// <summary>
        /// Monsters stay forward-only: the Cute Monsters pack has a single Walk take, their rigs are generic (no hips or
        /// spine to turn), and the monster AI always walks along its facing (MonsterAi, MovementSim.StepAlong).
        /// </summary>
        private static AnimationSet MonsterSet(string name, string modelPath, MonsterArt art)
        {
            var set = LoadOrCreate($"{OutputFolder}/AnimationSets/{name}.asset");
            set.idle = Clip(modelPath, MonsterIdleTake);
            set.walk = Clip(modelPath, MonsterWalkTake);
            set.run = null;
            set.walkReferenceSpeedCmPerSec = ReferenceSpeed(set.walk, DefaultWalkCmPerSec);
            set.runReferenceSpeedCmPerSec = set.walkReferenceSpeedCmPerSec;
            set.walkBackward = new LocomotionClip();
            set.walkLeft = new LocomotionClip();
            set.walkRight = new LocomotionClip();
            set.runBackward = new LocomotionClip();
            set.runLeft = new LocomotionClip();
            set.runRight = new LocomotionClip();
            set.legTurnMaxDeg = 0f;
            set.legTurnHysteresisDeg = 0f;
            set.legTurnDegPerSec = 0f;
            set.groundContact = false; // no leg bones (generic rigs)
            set.groundContactToleranceCm = 0f;
            set.groundContactMaxCm = 0f;
            set.defaultAction = new ActionClipEntry { clip = Clip(modelPath, MonsterAttackTake), startupEnd = 0.4f, activeEnd = 0.6f };
            // Phase timings come from ActionDef; the marks only say where the take's wind-up and hit are.
            set.actions = art.Actions
                .Select(a => new ActionClipEntry { actionId = a.ActionId, clip = Clip(modelPath, a.Take), startupEnd = 0.4f, activeEnd = 0.6f })
                .ToList();
            set.flinch = Clip(modelPath, MonsterHitTake);
            set.stagger = Clip(modelPath, MonsterHitTake);
            set.death = Clip(modelPath, MonsterDeathTake);
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

        /// <summary>
        /// The ground speed at which <paramref name="clip"/>'s feet stay planted on the humanoid <paramref name="model"/> while
        /// the body moves toward <paramref name="direction"/> of its facing, in cm/s at the prefab's own scale: one cycle is
        /// sampled the way <see cref="VisualAnimator"/> plays it (root motion off, root Y baked into the pose by
        /// <see cref="BakeRootHeight"/>, backward for a <paramref name="reverse"/> slot), and each foot's speed against that
        /// direction is averaged over the samples where it is within <see cref="StanceToleranceM"/> of its lowest point (its
        /// stance). The fallback when nothing can be measured.
        /// </summary>
        public static float StanceSpeed(GameObject model, AnimationClip clip, float fallbackCmPerSec,
            LocomotionDirection direction = LocomotionDirection.Forward, bool reverse = false)
        {
            // The direction in the model's space (x right, z forward); a stance foot moves the other way.
            var along = direction switch
            {
                LocomotionDirection.Right => new Vector2(1f, 0f),
                LocomotionDirection.Backward => new Vector2(0f, -1f),
                LocomotionDirection.Left => new Vector2(-1f, 0f),
                _ => new Vector2(0f, 1f),
            };
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(model);
            var graph = PlayableGraph.Create("StanceSpeed");
            try
            {
                var animator = instance.GetComponent<Animator>();
                if (animator == null || !animator.isHuman)
                    return fallbackCmPerSec;
                var feet = new[]
                {
                    Sole(animator, HumanBodyBones.LeftToes, HumanBodyBones.LeftFoot),
                    Sole(animator, HumanBodyBones.RightToes, HumanBodyBones.RightFoot),
                };
                animator.applyRootMotion = false;
                var playable = AnimationClipPlayable.Create(graph, clip);
                playable.SetApplyFootIK(false);
                playable.SetSpeed(0);
                AnimationPlayableOutput.Create(graph, "Sample", animator).SetSourcePlayable(playable);

                var height = new float[feet.Length, StanceSamples];
                var forward = new float[feet.Length, StanceSamples]; // along the direction
                var space = instance.transform;
                for (int i = 0; i < StanceSamples; i++)
                {
                    playable.SetTime(clip.length * i / StanceSamples);
                    graph.Evaluate(0f);
                    for (int f = 0; f < feet.Length; f++)
                    {
                        var p = space.InverseTransformPoint(feet[f].position);
                        height[f, i] = p.y;
                        forward[f, i] = p.x * along.x + p.z * along.y;
                    }
                }

                double distanceM = 0, timeS = 0;
                float stepS = clip.length / StanceSamples;
                for (int f = 0; f < feet.Length; f++)
                {
                    float lowest = float.MaxValue;
                    for (int i = 0; i < StanceSamples; i++)
                        lowest = Math.Min(lowest, height[f, i]);
                    for (int i = 0; i < StanceSamples; i++)
                    {
                        int next = (i + 1) % StanceSamples; // the clip loops
                        if (height[f, i] > lowest + StanceToleranceM || height[f, next] > lowest + StanceToleranceM)
                            continue;
                        // Played backward, the foot goes from the next sample to this one.
                        distanceM += reverse ? forward[f, next] - forward[f, i] : forward[f, i] - forward[f, next];
                        timeS += stepS;
                    }
                }
                float cmPerSec = timeS > 0 ? (float)(distanceM / timeS) * 100f : 0f;
                return cmPerSec > 10f ? Mathf.Round(cmPerSec) : fallbackCmPerSec;
            }
            finally
            {
                graph.Destroy();
                UnityEngine.Object.DestroyImmediate(instance);
            }
        }

        /// <summary>The toes bone (it stays on the ground longest), else the foot (avatars without toes).</summary>
        private static Transform Sole(Animator animator, HumanBodyBones toes, HumanBodyBones foot)
        {
            var bone = animator.GetBoneTransform(toes);
            return bone != null ? bone : animator.GetBoneTransform(foot);
        }

        // How a stance is found by StanceSpeed: samples per cycle, and how far above its lowest point a foot still stands.
        private const int StanceSamples = 240;
        private const float StanceToleranceM = 0.02f;

        /// <summary>
        /// Every take the builder puts in an animation set, with its model file (locomotion, actions, channel loops,
        /// reactions of the characters and the monsters); each must exist (<see cref="Clip"/>), checked by the EditMode tests.
        /// </summary>
        public static IEnumerable<(string Model, string Take)> ReferencedTakes()
        {
            foreach (var take in new[] { IdleTake, WalkTake, JogTake, FlinchTake, DeathTake })
                yield return (Ual1, take);
            yield return (Ual2, StaggerTake);
            foreach (var character in CharacterList)
            {
                yield return (Ual1, character.Attack);
                if (character.ChannelLoop != null)
                    yield return (Ual1, character.ChannelLoop);
                foreach (var action in character.Actions)
                {
                    yield return (action.Library, action.Take);
                    if (action.ChannelLoop != null)
                        yield return (action.Library, action.ChannelLoop);
                }
            }
            foreach (var monster in MonsterList)
            {
                string model = $"{Monsters}/{monster.Model}";
                foreach (var take in new[] { MonsterIdleTake, MonsterWalkTake, MonsterAttackTake, MonsterHitTake, MonsterDeathTake })
                    yield return (model, take);
                foreach (var (_, take) in monster.Actions)
                    yield return (model, take);
            }
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
