#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Game.Gameplay.Dev;
using Game.Gameplay.Entities;
using Game.Gameplay.Streaming;
using Game.UI.Config;
using Game.UI.HUD;
using Game.UI.ViewModels.Hud;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;
using Shared.World;
using MoveKeys = Shared.Protocol.MoveKeys;
using UnityEngine;
using UnityEngine.InputSystem.Controls;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;

namespace Game.Dev.Qa
{
    /// <summary>
    /// The QA smoke scenario (backlog J): title, dev login, server selection, a character (created if the account has
    /// none, of the class <c>-qaClass</c> names or else the first allowed one), enter world, send a local chat line and see
    /// its echo, walk to the demon, kill it with
    /// the class's kit (QaFightPlan), shoot the kit's first projectile at a wall (not applicable to a kit without one, such as
    /// the warrior's), wait for the respawn. The class's kit is its <see cref="ActionBindings"/> row (backlog E). It drives the game only through <see cref="QaInput"/> (virtual keyboard and mouse) and
    /// <see cref="QaLoginDriver"/> (submit events on the login buttons), observes through <see cref="QaWorldTap"/>, and
    /// writes a screenshot and a JSON file per step under the run folder (<see cref="QaRunWriter"/>). Needs the Auth API
    /// (dev login) and the GameServer running; see <c>tools/qa/README.md</c>.
    /// </summary>
    public sealed class QaScenario : IDisposable
    {
        /// <summary>The login scene (the first scene of the build settings, made by <c>LoginSceneBuilder</c>).</summary>
        public const string LoginScene = "Assets/Game/UI/Login/Login.unity";

        public static readonly string[] StepNames =
        {
            "title", "dev-login", "server-select", "character", "enter-world", "chat-echo", "walk-to-demon", "kill-demon",
            "pick-up-drop", "arrow-wall", "wait-respawn",
        };

        // Layout ids of the login elements (data/tables/LoginLayout.json).
        private const string PressAnyButton = "press_any_button", TitleMessage = "title_message", Badge = "steam_badge";
        private const string ServerTable = "server_table", EnterServer = "enter_server_button", ServerMessage = "server_message";
        private const string SlotList = "slot_list", CreateButton = "btn_create", EnterButton = "btn_enter";
        private const string CharMessage = "char_message", CharDetail = "character_detail";
        private const string NameField = "create_name", ClassList = "create_class_list", CreateConfirm = "btn_create_confirm";
        private const string CreateMessage = "create_message";
        private const int MaxLogLinesPerStep = 300;

        private readonly ConfigSet _config;
        private readonly LoginConfig _login;
        private readonly WorldGrid _grid;
        private readonly QaRunWriter _writer;
        private readonly QaScreenshots _shots = new QaScreenshots(QaTimeouts.ShotWidth, QaTimeouts.ShotHeight);
        private readonly QaLoginDriver _ui = new QaLoginDriver();
        private readonly QaWorldTap _tap;
        private readonly Stopwatch _clock = new Stopwatch();
        private QaInput? _input;
        private QaStepResult? _step;
        private int _notesLogged;

        // Facts carried from one step to the next.
        private string _monsterId = "";
        private ushort _monsterNetId;
        private QaMarker? _monsterMarker;
        private uint _demonId;
        private uint _deathTick;
        private TopDownCameraRig? _rig;
        private EntityViews? _views;
        private readonly string _requestedClass;
        private ActionBindings? _bindings;
        private HudRoot? _hud;

        private QaScenario(string runDir, ConfigSet config, WorldGrid grid)
        {
            _config = config;
            _login = LoginConfig.From(config);
            _grid = grid;
            _writer = new QaRunWriter(runDir, StepNames, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            _writer.Result.devLogin = CommandLineValue("-devLogin");
            _requestedClass = CommandLineValue("-qaClass");
            _tap = new QaWorldTap(config, grid, _login.Settings.WorldScene);
        }

        public QaRunResult Result => _writer.Result;

        /// <summary>Loads the client's config tables and the world grid; false with the reason when they do not load.</summary>
        public static bool TryCreate(string runDir, out QaScenario? scenario, out string error)
        {
            scenario = null;
            if (!ClientConfig.TryLoad(out var config, out error))
                return false;
            // The world the dev scene predicts with (PredictionDevClient.worldId); TODO(B4): from the enter-world message.
            if (!ClientWorld.TryLoadGrid("m1", out var grid, out error) || grid == null)
                return false;
            scenario = new QaScenario(runDir, config, grid);
            return true;
        }

        /// <summary>Plays every step until one fails, then writes <c>result.json</c>.</summary>
        public IEnumerator Run()
        {
            _clock.Start();
            Application.logMessageReceived += OnLog;
            int frameRate = Application.targetFrameRate;
            // Batch mode has no vsync and would run thousands of frames a second; play at a player's frame rate.
            Application.targetFrameRate = QaTimeouts.FrameRate;
            try
            {
                _input = new QaInput();
                var bodies = new Func<QaStepResult, IEnumerator>[]
                {
                    Title, DevLogin, ServerSelect, Character, EnterWorld, ChatEcho, WalkToDemon, KillDemon, PickUpDrop,
                    ArrowAtWall, WaitRespawn,
                };
                for (int i = 0; i < bodies.Length; i++)
                {
                    var step = _writer.Result.steps[i];
                    yield return RunStep(step, bodies[i]);
                    if (!step.Cleared)
                        break;
                }
            }
            finally
            {
                Application.logMessageReceived -= OnLog;
                Application.targetFrameRate = frameRate;
                _input?.ReleaseAll();
                _writer.Finish(_clock.ElapsedMilliseconds);
            }
        }

        public void Dispose()
        {
            _input?.Dispose();
            _input = null;
            _shots.Dispose();
            _tap.Dispose();
        }

        // ---- steps

        private IEnumerator Title(QaStepResult step)
        {
#if UNITY_EDITOR
            UnityEditor.SceneManagement.EditorSceneManager.LoadSceneInPlayMode(LoginScene, new LoadSceneParameters(LoadSceneMode.Single));
#else
            SceneManager.LoadScene(LoginScene);
#endif
            yield return WaitUntil(() => _ui.TryAttach() && _ui.IsShown(LoginScreens.Title), QaTimeouts.UiSeconds, "the title screen");
            _shots.RedirectPanels();
            Check(step, "title screen shown", true, "title visible", "title visible");
            yield return Shot(step);
        }

        private IEnumerator DevLogin(QaStepResult step)
        {
            string ready = _login.Text.Get(UiTextIds.PressAnyButton);
            yield return WaitUntil(() => _ui.TextOf(LoginScreens.Title, PressAnyButton) == ready || _ui.ErrorOn(LoginScreens.Title, TitleMessage).Length > 0,
                QaTimeouts.UiSeconds, "the sign-in to finish");
            string error = _ui.ErrorOn(LoginScreens.Title, TitleMessage);
            Check(step, "signed in (dev login)", error.Length == 0, "no error, prompt '" + ready + "'", error.Length > 0 ? error : ready);
            Check(step, "account badge", true, "dev login badge", _ui.TextOf(LoginScreens.Title, Badge));
            yield return Shot(step);
            Require(step);
            yield return Press(_input!.Keyboard.spaceKey);
            yield return WaitUntil(() => _ui.IsShown(LoginScreens.ServerSelect), QaTimeouts.UiSeconds, "the server selection after a key press");
        }

        private IEnumerator ServerSelect(QaStepResult step)
        {
            var server = _login.Servers[0];
            yield return WaitUntil(() => _ui.ButtonsIn(LoginScreens.ServerSelect, ServerTable).Count > 0, QaTimeouts.UiSeconds, "the server rows");
            var rows = _ui.ButtonsIn(LoginScreens.ServerSelect, ServerTable);
            Check(step, "server rows", rows.Count == _login.Servers.Count, _login.Servers.Count + " (ServerDirectory)", rows.Count.ToString(CultureInfo.InvariantCulture));
            bool named = rows[0].Query<Label>().ToList().Any(label => label.text == server.Name);
            Check(step, "first row names the server", named, server.Name, named ? server.Name : "not found");
            QaLoginDriver.Click(rows[0]);
            yield return WaitUntil(() => QaLoginDriver.CanClick(_ui.Find<Button>(LoginScreens.ServerSelect, EnterServer)),
                QaTimeouts.UiSeconds, "Enter server to be enabled");
            yield return Shot(step);
            Require(step);
            QaLoginDriver.Click(_ui.Find<Button>(LoginScreens.ServerSelect, EnterServer));
            yield return WaitUntil(() => _ui.IsShown(LoginScreens.CharacterSelect) || _ui.ErrorOn(LoginScreens.ServerSelect, ServerMessage).Length > 0,
                QaTimeouts.UiSeconds, "the character selection");
            string error = _ui.ErrorOn(LoginScreens.ServerSelect, ServerMessage);
            Check(step, "character list loaded", error.Length == 0, "character selection shown", error.Length > 0 ? error : "shown");
        }

        private IEnumerator Character(QaStepResult step)
        {
            string empty = _login.Text.Get(UiTextIds.EmptySlot);
            yield return WaitUntil(() => QaLoginDriver.CanClick(_ui.Find<Button>(LoginScreens.CharacterSelect, CreateButton)),
                QaTimeouts.UiSeconds, "the character list");
            var existing = _ui.ButtonsIn(LoginScreens.CharacterSelect, SlotList)
                .FirstOrDefault(card => card.Query<Label>().ToList().All(label => label.text != empty));
            if (existing != null)
            {
                step.log.Add("the account has a character: selecting it");
                QaLoginDriver.Click(existing);
            }
            else
            {
                QaLoginDriver.Click(_ui.Find<Button>(LoginScreens.CharacterSelect, CreateButton));
                yield return WaitUntil(() => _ui.IsShown(LoginScreens.CharacterCreate), QaTimeouts.UiSeconds, "the creation screen");
                string name = NewName(Math.Min(_login.Creation.NameMaxLength, 10));
                QaLoginDriver.Type(_ui.Find<TextField>(LoginScreens.CharacterCreate, NameField), name);
                yield return null;
                var classes = _ui.ButtonsIn(LoginScreens.CharacterCreate, ClassList);
                if (classes.Count == 0)
                    throw new QaFailure("the creation screen lists no class");
                // The buttons follow CharacterCreationConfig.allowedInitialClasses.
                var allowed = _login.Creation.AllowedInitialClasses.ToList();
                int classIndex = _requestedClass.Length == 0 ? 0 : allowed.IndexOf(_requestedClass);
                if (classIndex < 0 || classIndex >= classes.Count)
                    throw new QaFailure($"-qaClass '{_requestedClass}' is not an allowed initial class ({string.Join(", ", allowed)})");
                QaLoginDriver.Click(classes[classIndex]);
                yield return WaitUntil(() => QaLoginDriver.CanClick(_ui.Find<Button>(LoginScreens.CharacterCreate, CreateConfirm)),
                    QaTimeouts.UiSeconds, "Create to be enabled (name '" + name + "')");
                step.log.Add($"creating '{name}' ({allowed[classIndex]})");
                yield return Shot(step, "create");
                QaLoginDriver.Click(_ui.Find<Button>(LoginScreens.CharacterCreate, CreateConfirm));
                yield return WaitUntil(() => _ui.IsShown(LoginScreens.CharacterSelect) || _ui.ErrorOn(LoginScreens.CharacterCreate, CreateMessage).Length > 0,
                    QaTimeouts.UiSeconds, "the new character");
                string createError = _ui.ErrorOn(LoginScreens.CharacterCreate, CreateMessage);
                Check(step, "character created", createError.Length == 0, "created", createError.Length > 0 ? createError : "created");
                Require(step);
            }
            yield return WaitUntil(() => QaLoginDriver.CanClick(_ui.Find<Button>(LoginScreens.CharacterSelect, EnterButton))
                    || _ui.ErrorOn(LoginScreens.CharacterSelect, CharMessage).Length > 0,
                QaTimeouts.UiSeconds, "a selected character");
            string error = _ui.ErrorOn(LoginScreens.CharacterSelect, CharMessage);
            var detail = _ui.Find<VisualElement>(LoginScreens.CharacterSelect, CharDetail)?.Q<Label>()?.text ?? "";
            Check(step, "character selected", error.Length == 0, "Enter world enabled", error.Length > 0 ? error : detail);
            yield return Shot(step);
        }

        private IEnumerator EnterWorld(QaStepResult step)
        {
            QaLoginDriver.Click(_ui.Find<Button>(LoginScreens.CharacterSelect, EnterButton));
            yield return WaitUntil(() => (_tap.Attached && _tap.HasOwn && _tap.Registry.HasOwnResources)
                                         || (_ui.TryAttach() && _ui.ErrorOn(LoginScreens.CharacterSelect, CharMessage).Length > 0),
                QaTimeouts.EnterWorldSeconds, "the world scene and the first own snapshot");
            if (!_tap.Attached)
                throw new QaFailure("enter world refused: " + _ui.ErrorOn(LoginScreens.CharacterSelect, CharMessage));
            yield return WaitUntil(() => FindWorldObjects(), QaTimeouts.UiSeconds, "the camera rig and the local player view");
            var own = _tap.Registry.OwnResources;
            Check(step, "world scene", SceneManager.GetActiveScene().path == _login.Settings.WorldScene, _login.Settings.WorldScene, SceneManager.GetActiveScene().path);
            Check(step, "own resources", own.MaxHp > 0, "HP/MP/stamina in snapshots",
                $"HP {own.Hp}/{own.MaxHp} MP {own.Mp}/{own.MaxMp} ST {own.Stamina}/{own.MaxStamina}");
            Check(step, "own position", true, "server position", $"({_tap.OwnXCm}, {_tap.OwnYCm}) cm");
            // Backlog E: the own spawn names the class (ClassDef net id); its ActionBindings row is the kit played below.
            yield return WaitUntil(() => _tap.Registry.OwnDefId != 0, QaTimeouts.UiSeconds, "the own spawn (the class)");
            var classDef = Rows<ClassDef>().FirstOrDefault(c => c.NetId == _tap.Registry.OwnDefId);
            string expectedClass = _requestedClass.Length > 0 ? _requestedClass : "a ClassDef";
            Check(step, "class", classDef != null && (_requestedClass.Length == 0 || classDef.Id == _requestedClass), expectedClass,
                classDef?.Id ?? "net id " + _tap.Registry.OwnDefId);
            Require(step);
            _writer.Result.classId = classDef!.Id;
            _bindings = ActionBindings.For(_config, classDef.Id) ?? throw new QaFailure("no ActionBindings row for " + classDef.Id);
            step.log.Add($"class {classDef.Id}: normal attack {_bindings.NormalAttack}, slots {string.Join(",", _bindings.SkillSlots)}");
            // Visuals load asynchronously; a view with no renderer at all after that is a broken prefab or import.
            float waitVisual = Time.realtimeSinceStartup;
            while (CountRenderers(_views!.LocalView!) == 0 && Time.realtimeSinceStartup - waitVisual < QaTimeouts.VisualSeconds)
                yield return null;
            int drawn = CountRenderers(_views.LocalView!);
            Check(step, "local player drawn", drawn > 0, "renderers on the local view (if 0: reimport the visuals, unity/Assets/Game/Dev/README.md)",
                DescribeView(_views.LocalView!, _rig!.Camera));
            // Backlog I: the HUD resource panel shows the own HP from the snapshots, with the UiStrings template.
            _hud = UnityEngine.Object.FindAnyObjectByType<HudRoot>();
            yield return WaitUntil(() => _hud != null && _hud.ResourcePanel != null && _hud.ResourcePanel.ValueText(0).Length > 0,
                QaTimeouts.UiSeconds, "the HUD resource panel");
            var now = _tap.Registry.OwnResources;
            string expectedHp = _login.Text.Get(HudTextIds.ResourceValue,
                new TextArg("current", now.Hp.ToString(CultureInfo.InvariantCulture)), new TextArg("max", now.MaxHp.ToString(CultureInfo.InvariantCulture)));
            string shownHp = _hud!.ResourcePanel!.ValueText(0);
            Check(step, "HUD shows the own HP", shownHp == expectedHp, expectedHp, shownHp);
            yield return Settle();
            yield return Shot(step);
        }

        /// <summary>
        /// Local chat (spec 9.12, R-6.5-01): Enter opens the input, gameplay keys are suspended while it is open (held W
        /// does not move the player), Enter sends the line, and the server's echo shows it under the own display name.
        /// </summary>
        private IEnumerator ChatEcho(QaStepResult step)
        {
            var chat = _hud?.Chat ?? throw new QaFailure("no HUD chat panel");
            var model = _hud.Models!.Chat;
            int linesBefore = model.Lines.Count;
            yield return Press(_input!.Keyboard.enterKey);
            yield return WaitUntil(() => model.InputOpen, QaTimeouts.UiSeconds, "the chat input to open");
            Check(step, "Enter opens the chat input", chat.InputVisible, "input visible", chat.InputVisible ? "visible" : "hidden");

            int x = _tap.OwnXCm, y = _tap.OwnYCm;
            _input.Hold(_input.Keyboard.wKey, true);
            yield return Wait(QaTimeouts.ChatTypingHoldSeconds);
            _input.Hold(_input.Keyboard.wKey, false);
            yield return Wait(QaTimeouts.ChatTypingHoldSeconds);
            Check(step, "gameplay keys suspended while typing", _tap.OwnXCm == x && _tap.OwnYCm == y, $"({x}, {y}) cm",
                $"({_tap.OwnXCm}, {_tap.OwnYCm}) cm");

            const string line = "QA smoke: hello <b>world</b>";
            chat.Draft = line;
            yield return Shot(step, "typing");
            yield return Press(_input.Keyboard.enterKey);
            uint own = _tap.Registry.OwnEntityId ?? 0;
            yield return WaitUntil(() => model.Lines.Count > linesBefore, QaTimeouts.ChatEchoSeconds, "the echo of the chat line");
            var echo = model.Lines[model.Lines.Count - 1];
            Check(step, "echo of the own line", echo.Kind == ChatLineKind.Player && echo.Text == line && echo.SenderEntityId == own,
                $"'{line}' from entity {own}", $"{echo.Kind} '{echo.Text}' from entity {echo.SenderEntityId}");
            Check(step, "sender name from DisplayIdentity", echo.Name.Length > 0, "the own character name", echo.Name);
            Check(step, "input closed after sending", !model.InputOpen, "closed", model.InputOpen ? "open" : "closed");
            yield return Settle();
            yield return Shot(step);
        }

        private IEnumerator WalkToDemon(QaStepResult step)
        {
            PickMonster();
            var marker = _monsterMarker!;
            int approachCm = ApproachCm();
            step.log.Add($"target {_monsterId} at marker {marker.id} ({marker.xCm}, {marker.yCm}); stop within {approachCm} cm");

            float started = Time.realtimeSinceStartup;
            int firstHit = _tap.Hits.Count;
            var walk = new ApproachResult();
            yield return Approach(approachCm, QaTimeouts.WalkSeconds, walk);
            if (walk.Died)
                throw new QaFailure("the player died on the way to the demon: " + DescribeDeath(firstHit));
            Check(step, "demon in sight", _demonId != 0, _monsterId + " entity", "entity " + _demonId);
            Check(step, "reached the demon", walk.DistanceCm <= approachCm, "<= " + approachCm + " cm",
                walk.DistanceCm.ToString("0", CultureInfo.InvariantCulture) + " cm");
            step.log.Add($"walked in {Time.realtimeSinceStartup - started:0.0} s to ({_tap.OwnXCm}, {_tap.OwnYCm})");
            yield return Settle();
            yield return Shot(step);
        }

        /// <summary>
        /// Fights the demon with the class's kit until it dies (<see cref="QaFightPlan"/>, the same for every class: its
        /// ActionBindings row): the damaging skills when they are ready (stamina and MP from the snapshots, cooldowns from
        /// the client's predicted action machine), the normal attack in between, a heal on itself when HP is low, or a
        /// back-off when it has none ready. The demon fights back (monster AI): the step logs the player's own HP, the damage
        /// taken and every action it used, and when the player dies it waits for the respawn (<c>playerRespawnDelayMs</c>)
        /// and walks back to fight again, up to <see cref="QaTimeouts.KillLives"/> lives within <see cref="QaTimeouts.KillSeconds"/>.
        /// </summary>
        private IEnumerator KillDemon(QaStepResult step)
        {
            var bindings = _bindings!;
            var kit = QaFightPlan.Kit(_config, bindings);
            var dev = UnityEngine.Object.FindAnyObjectByType<PredictionDevClient>()
                      ?? throw new QaFailure("no PredictionDevClient in the world scene (the predicted cooldowns)");
            var monster = MonsterDef.Find(_config, _monsterId)!;
            int respawnMs = _config.Single<StartingValuesM1>().PlayerRespawnDelayMs;
            int reachCm = ApproachCm();
            var used = new Dictionary<string, int>();
            // The server refused these as still cooling down: held off for their ActionDef cooldown (the client gives a
            // predicted cooldown back when the server interrupts the action, so the predicted one can be short).
            var heldUntil = new Dictionary<string, float>();
            int seenRefusals = _tap.Refusals.Count;
            int backOffs = 0;
            step.log.Add("kit: " + string.Join(", ", kit.Select(a =>
                $"{a.Id} ({a.Button}, {(a.Damages ? a.DamagePercent + "%, reach " + a.ReachCm + " cm" : a.HealsSelf ? "heal" : "no damage")})")));
            int firstHit = _tap.Hits.Count;
            byte firstBucket = _tap.Registry.Entities.TryGetValue(_demonId, out var demon) ? demon.HpBucket : (byte)0;
            byte lowestBucket = firstBucket;
            int lowestHp = _tap.Registry.OwnResources.Hp;
            var deaths = new List<string>();
            double lastSeenCm = double.MaxValue;
            int presses = 0;
            bool fightShot = false;
            // Backlog I: the demon's name plate (UiStrings name) and its HP bar once damaged, and damage numbers.
            string monsterName = _login.Text.Get(UiStringCoverage.MonsterId(monster.Id));
            bool sawPlate = false, sawHpBar = false;
            long numbersBefore = _hud?.Models?.DamageNumbers.TotalSpawned ?? 0;
            long telegraphsBefore = _hud?.Models?.Telegraphs.TotalStarted ?? 0;
            float started = Time.realtimeSinceStartup;
            step.log.Add($"own HP {OwnHp()} at the start");
            while (true)
            {
                bool visible = _tap.Registry.Entities.TryGetValue(_demonId, out demon);
                if (!visible && (KilledHitOn(_demonId, firstHit) || (!OwnDead() && lastSeenCm <= QaTimeouts.GoneNearCm)))
                    break;
                float elapsed = Time.realtimeSinceStartup - started;
                if (elapsed > QaTimeouts.KillSeconds)
                    throw new QaFailure($"the demon still lives after {QaTimeouts.KillSeconds} s ({presses} attacks, HP bucket " +
                                        $"{(visible ? demon!.HpBucket.ToString(CultureInfo.InvariantCulture) : "out of sight")}); " +
                                        OwnState(firstHit, lowestHp, deaths));
                if (_tap.Disconnected)
                    throw new QaFailure("disconnected during the fight; " + OwnState(firstHit, lowestHp, deaths));
                lowestHp = Math.Min(lowestHp, _tap.Registry.OwnResources.Hp);

                if (OwnDead())
                {
                    Walk(null);
                    _input!.ReleaseAll();
                    deaths.Add(DescribeDeath(firstHit));
                    lastSeenCm = double.MaxValue;
                    step.log.Add($"the player died ({deaths.Count} of {QaTimeouts.KillLives} lives): {deaths[deaths.Count - 1]}");
                    step.log.Add($"used so far: {Used(used)}; {backOffs} back-offs; {_tap.Hits.Skip(firstHit).Count(h => h.Entry.Kind == HitKind.Healed)} heals landed");
                    if (deaths.Count >= QaTimeouts.KillLives)
                    {
                        AddOwnChecks(step, firstHit, lowestHp, deaths);
                        throw new QaFailure($"the player died {deaths.Count} times: {string.Join("; ", deaths)}");
                    }
                    float diedAt = Time.realtimeSinceStartup;
                    yield return WaitUntil(() => !OwnDead(), respawnMs / 1000f + QaTimeouts.RespawnMarginSeconds,
                        $"the player's respawn (playerRespawnDelayMs {respawnMs})");
                    step.log.Add($"respawned after {Time.realtimeSinceStartup - diedAt:0.0} s at ({_tap.OwnXCm}, {_tap.OwnYCm}) " +
                                 $"with HP {OwnHp()}; walking back to the demon");
                    yield return Approach(reachCm, Math.Max(QaTimeouts.KillSeconds - (Time.realtimeSinceStartup - started), 1f), new ApproachResult());
                    continue;
                }
                if (!visible)
                {
                    // Out of sight (the player respawned far away, or the demon leashed home): walk back to it.
                    yield return Approach(reachCm, Math.Max(QaTimeouts.KillSeconds - elapsed, 1f), new ApproachResult());
                    continue;
                }
                lowestBucket = Math.Min(lowestBucket, demon!.HpBucket);
                sawPlate |= _hud?.NamePlates != null && _hud.NamePlates.VisibleWithName(monsterName) > 0;
                sawHpBar |= _hud?.NamePlates != null && _hud.NamePlates.VisibleHpBars() > 0;
                if (!QaWorldTap.TryGroundCm(demon, out int x, out int y))
                {
                    yield return null;
                    continue;
                }
                var at = FixVec2.FromCm(x, y);
                lastSeenCm = DistanceCm(Own(), at);
                AimAt(at);
                if (lastSeenCm > reachCm)
                {
                    Walk(at); // the demon moved (monster AI): follow it
                    yield return null;
                    continue;
                }
                Walk(null);
                for (; seenRefusals < _tap.Refusals.Count; seenRefusals++)
                {
                    var refusal = _tap.Refusals[seenRefusals];
                    var refused = kit.FirstOrDefault(a => a.Def.NetId == refusal.ActionNetId);
                    if (refused != null && refusal.Reason == ActionReason.OnCooldown)
                        heldUntil[refused.Id] = Time.realtimeSinceStartup + refused.Def.CooldownMs / 1000f;
                }
                var own = _tap.Registry.OwnResources;
                var choice = QaFightPlan.Choose(kit, new QaFightState(own.Hp, own.MaxHp, own.Mp, own.Stamina, lastSeenCm,
                    a => PredictedReady(dev, a) && (!heldUntil.TryGetValue(a.Id, out float until) || Time.realtimeSinceStartup >= until)));
                if (choice.Move == QaFightMove.Wait)
                {
                    yield return null;
                    continue;
                }
                if (choice.Move == QaFightMove.BackOff)
                {
                    backOffs++;
                    step.log.Add($"HP {OwnHp()} and no heal ready: backing off {QaTimeouts.BackOffCm} cm");
                    yield return BackOff(at);
                    continue;
                }
                var action = choice.Action!;
                used[action.Id] = used.TryGetValue(action.Id, out int count) ? count + 1 : 1;
                yield return Press(_input!.ButtonFor(action.Button));
                presses++;
                // Until the predicted machine is idle again: a refused or interrupted action frees the player at once.
                float pressed = Time.realtimeSinceStartup;
                while (dev.Predictor != null && dev.Predictor.Actions.IsBusy && !OwnDead()
                       && Time.realtimeSinceStartup - pressed < action.Def.TotalMs() / 1000f + QaTimeouts.PressSeconds)
                    yield return null;
                if (!fightShot && OurHitsOn(_demonId, firstHit).Any())
                {
                    fightShot = true;
                    yield return Shot(step, "fight");
                }
            }
            Walk(null);
            var hits = OurHitsOn(_demonId, firstHit).ToList();
            int damage = hits.Sum(hit => hit.Entry.Amount);
            var despawn = _tap.Events.LastOrDefault(e => !e.Spawned && e.EntityId == _demonId);
            _deathTick = despawn.EntityId == _demonId ? despawn.Tick : _tap.ServerTick;
            step.log.Add($"{presses} actions, {hits.Count} hits, {damage} damage, death at tick {_deathTick}");
            step.log.Add($"used: {Used(used)}; {backOffs} back-offs");
            Check(step, "hit messages on the demon", hits.Count > 0, "> 0 hits from us", hits.Count.ToString(CultureInfo.InvariantCulture));
            Check(step, "HP bucket went down", lowestBucket < firstBucket, "below " + firstBucket, lowestBucket.ToString(CultureInfo.InvariantCulture));
            Check(step, "killing hit", hits.Any(hit => hit.Entry.Kind == HitKind.Killed), "a Killed hit", string.Join(",", hits.Select(hit => hit.Entry.Kind).Distinct()));
            Check(step, "damage covers max HP", damage >= monster.MaxHp, ">= " + monster.MaxHp + " (MonsterDef)", damage.ToString(CultureInfo.InvariantCulture));
            long numbers = (_hud?.Models?.DamageNumbers.TotalSpawned ?? 0) - numbersBefore;
            Check(step, "HUD damage numbers", numbers >= hits.Count, ">= " + hits.Count + " (our hits)", numbers.ToString(CultureInfo.InvariantCulture));
            Check(step, "HUD name plate", sawPlate, "'" + monsterName + "' above the demon", sawPlate ? "shown" : "not shown");
            Check(step, "HUD monster HP bar", sawHpBar, "shown once damaged", sawHpBar ? "shown" : "not shown");
            step.log.Add($"HUD: {numbers} damage numbers, {(_hud?.Models?.Telegraphs.TotalStarted ?? 0) - telegraphsBefore} monster telegraphs started");
            AddOwnChecks(step, firstHit, lowestHp, deaths);
            yield return Settle();
            yield return Shot(step);
        }

        /// <summary>
        /// Backlog G: the demon's loot lies where it died (a LootDrop spawn with an <c>ItemDef</c> net id). Walk next to it,
        /// press the pickup key, and check the answer and the bag the server sends once the pickup is saved.
        /// </summary>
        private IEnumerator PickUpDrop(QaStepResult step)
        {
            RemoteEntity? drop = null;
            yield return WaitUntil(() => (drop = NearestDrop()) != null, QaTimeouts.LootSeconds, "the demon's drop");
            var item = Rows<ItemDef>().FirstOrDefault(row => row.NetId == drop!.DefId);
            Check(step, "drop names an item", item != null, "an ItemDef net id", drop!.DefId.ToString(CultureInfo.InvariantCulture));
            Require(step);
            QaWorldTap.TryGroundCm(drop, out int dropX, out int dropY);
            var at = FixVec2.FromCm(dropX, dropY);
            int rangeCm = _config.Single<LootSettings>().PickupRangeCm;
            int nearCm = rangeCm * QaTimeouts.LootApproachPercentOfRange / 100;
            step.log.Add($"drop {drop.Id} ({item!.Id}) at ({dropX}, {dropY}); pickup range {rangeCm} cm, stopping within {nearCm} cm");
            float started = Time.realtimeSinceStartup;
            while (DistanceCm(Predicted(), at) > nearCm)
            {
                if (Time.realtimeSinceStartup - started > QaTimeouts.LootSeconds)
                    throw new QaFailure($"did not reach the drop in {QaTimeouts.LootSeconds:0} s");
                Walk(at);
                yield return null;
            }
            Walk(null);
            yield return Wait(3 * (float)_tap.TickMs / 1000f); // the server position catches up with the prediction
            yield return Shot(step, "drop");

            int answers = _tap.Pickups.Count;
            yield return Press(_input!.ButtonFor("Pickup"));
            yield return WaitUntil(() => _tap.Pickups.Count > answers, QaTimeouts.LootSeconds, "the pickup answer");
            var result = _tap.Pickups[answers];
            Check(step, "pickup accepted", result.Reason == PickupReason.None, nameof(PickupReason.None), result.Reason.ToString());
            Require(step);
            yield return WaitUntil(() => _tap.Inventory != null && _tap.Inventory.Slots.Any(s => s.ItemNetId == item.NetId),
                QaTimeouts.LootSeconds, "the saved bag with the item");
            var slot = _tap.Inventory!.Slots.First(s => s.ItemNetId == item.NetId);
            Check(step, "item in the saved bag", slot.Count >= result.Taken, item.Id + " x" + result.Taken, item.Id + " x" + slot.Count);
            yield return WaitUntil(() => !_tap.Registry.Entities.ContainsKey(drop.Id), QaTimeouts.LootSeconds, "the drop to leave the ground");
            Check(step, "drop gone from the ground", !_tap.Registry.Entities.ContainsKey(drop.Id), "despawned", "despawned");
            yield return Settle();
            yield return Shot(step);
        }

        /// <summary>The ground drop nearest to the player, if one is in sight.</summary>
        private RemoteEntity? NearestDrop()
        {
            RemoteEntity? best = null;
            double bestCm = double.MaxValue;
            foreach (var entity in _tap.Registry.Entities.Values)
            {
                if (entity.Kind != EntityKind.LootDrop || !QaWorldTap.TryGroundCm(entity, out int x, out int y))
                    continue;
                double cm = DistanceCm(Own(), FixVec2.FromCm(x, y));
                if (cm < bestCm)
                {
                    best = entity;
                    bestCm = cm;
                }
            }
            return best;
        }

        private IEnumerator ArrowAtWall(QaStepResult step)
        {
            // The kit's first action that shoots: the normal attack (left click), else the lowest hotbar slot.
            var bindings = _bindings!;
            var candidates = new List<(string ActionId, string Button)> { (bindings.NormalAttack, "Attack") };
            for (int i = 0; i < bindings.SkillSlots.Length; i++)
            {
                if (bindings.SkillSlots[i].Length > 0)
                    candidates.Add((bindings.SkillSlots[i], "Hotbar" + (i + 1).ToString(CultureInfo.InvariantCulture)));
            }
            var shooter = candidates.FirstOrDefault(c => Rows<ActionEffectDef>().Any(row => row.ActionId == c.ActionId && row.Kind == EffectKind.Projectile));
            if (shooter.ActionId == null)
                throw new QaNotApplicable($"the {_writer.Result.classId} kit shoots no projectile (ActionBindings)");
            var skill = ActionDef.Find(_config, shooter.ActionId)!;
            var effect = Rows<ActionEffectDef>().First(row => row.ActionId == skill.Id && row.Kind == EffectKind.Projectile);
            var arrow = ProjectileDef.Find(_config, effect.ProjectileId)!;
            var avoid = _tap.Registry.Entities.Values.Where(e => e.Kind == EntityKind.Monster)
                .Select(e => QaWorldTap.TryGroundCm(e, out int x, out int y) ? FixVec2.FromCm(x, y) : Own()).ToList();
            if (!QaNavigation.TryFindWall(_grid, Own(), QaTimeouts.ShotClearanceCm, arrow.RangeCm * QaTimeouts.WallPercentOfRange / 100,
                    QaTimeouts.WallRays, avoid, QaTimeouts.ShotClearanceCm, out var wall, out int wallCm))
                throw new QaFailure($"no wall within {arrow.RangeCm * QaTimeouts.WallPercentOfRange / 100} cm of ({_tap.OwnXCm}, {_tap.OwnYCm})");
            step.log.Add($"aiming at the wall at ({QaNavigation.Cm(wall.X):0}, {QaNavigation.Cm(wall.Y):0}), {wallCm} cm away; {skill.Id} on {shooter.Button}");
            AimAt(wall);
            yield return Wait(3 * (float)_tap.TickMs / 1000f);
            float waitStamina = Time.realtimeSinceStartup;
            while (_tap.Registry.OwnResources.Stamina < skill.StaminaCost && Time.realtimeSinceStartup - waitStamina < QaTimeouts.ProjectileSeconds)
                yield return null;

            int before = _tap.Projectiles.Count;
            yield return Press(_input!.ButtonFor(shooter.Button));
            yield return WaitUntil(() => _tap.Projectiles.Count > before, QaTimeouts.ProjectileSeconds, "the arrow to spawn");
            var shot = _tap.Projectiles[before];
            // The client draws projectiles one interpolation delay behind the server: wait that plus two ticks.
            var prediction = Game.Gameplay.Prediction.PredictionSettings.From(_config);
            yield return Wait((float)((prediction.InterpolationDelayMs + 2 * prediction.TickMs) / 1000.0));
            yield return Shot(step, "flight");
            yield return WaitUntil(() => shot.Reason != ProjectileDespawnReason.None, QaTimeouts.ProjectileSeconds, "the arrow to despawn");
            Check(step, "arrow spawned", shot.DefId == arrow.Id, arrow.Id, shot.DefId);
            Check(step, "stopped by the wall", shot.Reason == ProjectileDespawnReason.World, nameof(ProjectileDespawnReason.World), shot.Reason.ToString());
            if (shot.DespawnTick is uint end)
            {
                double flownCm = (end - shot.SeenAtTick + 1) * _tap.TickMs / 1000.0 * arrow.SpeedCmPerSec;
                step.log.Add($"despawned at tick {end}, about {flownCm:0} cm of flight for a wall {wallCm} cm away");
            }
            yield return Settle();
            yield return Shot(step);
        }

        private IEnumerator WaitRespawn(QaStepResult step)
        {
            var marker = _monsterMarker!;
            int respawnMs = _config.Single<StartingValuesM1>().MonsterRespawnMs;
            float timeout = (float)(respawnMs / 1000.0 - (_tap.ServerTick - _deathTick) * _tap.TickMs / 1000.0) + QaTimeouts.RespawnMarginSeconds;
            step.log.Add($"waiting for {_monsterId} at {marker.id}; monsterRespawnMs {respawnMs}, death at tick {_deathTick}");
            QaEntityEvent respawn = default;
            yield return Wait(QaTimeouts.MidWaitShotSeconds);
            yield return Shot(step, "waiting"); // the empty spawn point
            yield return WaitUntil(() => TryFindRespawn(marker, out respawn), Math.Max(timeout - QaTimeouts.MidWaitShotSeconds, 1f), "the demon's respawn");
            double delayMs = (respawn.Tick - _deathTick) * _tap.TickMs;
            Check(step, "respawned at its marker", true, "within " + QaTimeouts.RespawnMarkerRadiusCm + " cm of " + marker.id,
                $"entity {respawn.EntityId} at ({respawn.XCm}, {respawn.YCm})");
            Check(step, "respawn delay", delayMs >= respawnMs - _tap.TickMs && delayMs <= respawnMs + QaTimeouts.RespawnMarginSeconds * 1000,
                respawnMs + " ms (StartingValuesM1.monsterRespawnMs)", delayMs.ToString("0", CultureInfo.InvariantCulture) + " ms");
            Check(step, "full HP", respawn.HpBucket == Snapshot.TopHpBucket, Snapshot.TopHpBucket.ToString(CultureInfo.InvariantCulture),
                respawn.HpBucket.ToString(CultureInfo.InvariantCulture));
            yield return Settle();
            yield return Shot(step);
        }

        // ---- step plumbing

        private IEnumerator RunStep(QaStepResult step, Func<QaStepResult, IEnumerator> body)
        {
            _step = step;
            step.startedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            long startMs = _clock.ElapsedMilliseconds;
            Exception? failure = null;
            yield return Flatten(body(step), e => failure = e);
            FlushNotes(step);
            if (failure is QaNotApplicable notApplicable && step.checks.All(c => c.pass))
            {
                step.status = QaStatus.NotApplicable;
                step.message = notApplicable.Message;
            }
            else if (failure == null && step.checks.All(c => c.pass))
            {
                step.status = QaStatus.Pass;
            }
            else
            {
                step.status = QaStatus.Fail;
                step.message = failure is QaFailure ? failure.Message
                    : failure != null ? failure.GetType().Name + ": " + failure.Message
                    : "failed checks: " + string.Join(", ", step.checks.Where(c => !c.pass).Select(c => c.name));
                if (failure != null && !(failure is QaFailure))
                    step.log.Add(failure.ToString());
                _input?.ReleaseAll();
                yield return Shot(step, "failure");
            }
            step.durationMs = _clock.ElapsedMilliseconds - startMs;
            _writer.WriteStep(step);
            Debug.Log($"[QA] step {step.index} {step.name}: {step.status} in {step.durationMs} ms {step.message}");
            _step = null;
        }

        /// <summary>Runs nested enumerators as one coroutine and reports the first exception instead of throwing it.</summary>
        private static IEnumerator Flatten(IEnumerator root, Action<Exception> failed)
        {
            var stack = new Stack<IEnumerator>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var top = stack.Peek();
                bool moved;
                try
                {
                    moved = top.MoveNext();
                }
                catch (Exception e)
                {
                    failed(e);
                    yield break;
                }
                if (!moved)
                {
                    stack.Pop();
                    continue;
                }
                if (top.Current is IEnumerator nested)
                {
                    stack.Push(nested);
                    continue;
                }
                yield return top.Current;
            }
        }

        private static IEnumerator WaitUntil(Func<bool> condition, float seconds, string what)
        {
            float started = Time.realtimeSinceStartup;
            while (!condition())
            {
                if (Time.realtimeSinceStartup - started > seconds)
                    throw new QaFailure($"timed out after {seconds:0} s waiting for {what}");
                yield return null;
            }
        }

        private static IEnumerator Wait(float seconds)
        {
            float started = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - started < seconds)
                yield return null;
        }

        private static IEnumerator Settle()
        {
            for (int i = 0; i < QaTimeouts.SettleFrames; i++)
                yield return null;
        }

        private IEnumerator Press(ButtonControl button)
        {
            _input!.Hold(button, true);
            yield return Wait(QaTimeouts.PressSeconds);
            _input.Hold(button, false);
            yield return null;
        }

        private IEnumerator Shot(QaStepResult step, string suffix = "")
        {
            _shots.RedirectPanels();
            string relative = QaRunWriter.ShotPath(step, suffix);
            string error = "";
            yield return _shots.Capture(_writer.FullPath(relative), e => error = e);
            if (error.Length == 0)
                step.screenshots.Add(relative);
            else
                step.log.Add("screenshot failed: " + error);
        }

        private static void Check(QaStepResult step, string name, bool pass, string expected, string actual) =>
            step.checks.Add(new QaCheck { name = name, pass = pass, expected = expected, actual = actual });

        /// <summary>Stops the step when one of its checks failed so far.</summary>
        private static void Require(QaStepResult step)
        {
            var failed = step.checks.FirstOrDefault(c => !c.pass);
            if (failed != null)
                throw new QaFailure($"{failed.name}: expected {failed.expected}, got {failed.actual}");
        }

        private void OnLog(string condition, string stackTrace, LogType type)
        {
            if (_step == null || _step.log.Count >= MaxLogLinesPerStep)
                return;
            bool ours = condition.StartsWith("[PredictionDev]", StringComparison.Ordinal) || condition.StartsWith("[Login]", StringComparison.Ordinal);
            if (ours || type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                _step.log.Add(type + ": " + condition);
        }

        private void FlushNotes(QaStepResult step)
        {
            for (; _notesLogged < _tap.Notes.Count; _notesLogged++)
                step.log.Add(_tap.Notes[_notesLogged]);
        }

        // ---- world helpers

        private static int CountRenderers(Transform view)
        {
            int count = 0;
            foreach (var renderer in view.GetComponentsInChildren<Renderer>())
            {
                if (renderer.enabled)
                    count++;
            }
            return count;
        }

        /// <summary>What the camera can draw of a view: its renderers, their layers and shaders (for the step log).</summary>
        private static string DescribeView(Transform view, Camera camera)
        {
            var renderers = view.GetComponentsInChildren<Renderer>(true);
            var parts = renderers.Select(r =>
                $"{r.name}[{(r.enabled && r.gameObject.activeInHierarchy ? "on" : "off")}, layer {r.gameObject.layer}" +
                $"{((camera.cullingMask & (1 << r.gameObject.layer)) == 0 ? " culled" : "")}, " +
                $"{string.Join("/", r.sharedMaterials.Select(m => m == null ? "no material" : m.shader.name).Distinct())}]");
            return $"{renderers.Length} renderers at {view.position} scale {view.lossyScale}: {string.Join("; ", parts.Take(12))}";
        }

        private bool FindWorldObjects()
        {
            _rig = UnityEngine.Object.FindAnyObjectByType<TopDownCameraRig>();
            _views = UnityEngine.Object.FindAnyObjectByType<EntityViews>();
            return _rig != null && _views != null && _views.LocalView != null;
        }

        /// <summary>The monster of the spawn point nearest to the player (SpawnPoints and the world's markers).</summary>
        private void PickMonster()
        {
            var markers = QaMarkers.Load(ClientWorld.Directory("m1"));
            QaMarker? best = null;
            string bestMonster = "";
            double bestCm = double.MaxValue;
            foreach (var point in Rows<SpawnPoints>())
            {
                var marker = markers.FirstOrDefault(m => m.id == point.MarkerId);
                if (marker == null)
                    continue;
                double cm = DistanceCm(Own(), FixVec2.FromCm(marker.xCm, marker.yCm));
                if (cm < bestCm)
                {
                    best = marker;
                    bestMonster = point.MonsterId;
                    bestCm = cm;
                }
            }
            var monster = best == null ? null : MonsterDef.Find(_config, bestMonster);
            if (best == null || monster == null)
                throw new QaFailure("no monster spawn point in SpawnPoints matches a marker of the world");
            _monsterMarker = best;
            _monsterId = monster.Id;
            _monsterNetId = (ushort)monster.NetId;
        }

        private RemoteEntity? NearestDemon(QaMarker marker)
        {
            RemoteEntity? best = null;
            double bestCm = double.MaxValue;
            foreach (var entity in _tap.Registry.Entities.Values)
            {
                if (entity.Kind != EntityKind.Monster || entity.DefId != _monsterNetId || !QaWorldTap.TryGroundCm(entity, out int x, out int y))
                    continue;
                double cm = DistanceCm(FixVec2.FromCm(marker.xCm, marker.yCm), FixVec2.FromCm(x, y));
                if (cm < bestCm)
                {
                    best = entity;
                    bestCm = cm;
                }
            }
            return best;
        }

        private bool TryFindRespawn(QaMarker marker, out QaEntityEvent respawn)
        {
            foreach (var e in _tap.Events)
            {
                if (!e.Spawned || e.DefId != _monsterNetId || e.Kind != EntityKind.Monster || (int)(e.Tick - _deathTick) <= 0)
                    continue;
                if (DistanceCm(FixVec2.FromCm(marker.xCm, marker.yCm), FixVec2.FromCm(e.XCm, e.YCm)) <= QaTimeouts.RespawnMarkerRadiusCm)
                {
                    respawn = e;
                    return true;
                }
            }
            respawn = default;
            return false;
        }

        /// <summary>Where <see cref="Approach"/> ended: how far from the demon, or whether the player died on the way.</summary>
        private sealed class ApproachResult
        {
            public double DistanceCm = double.MaxValue;
            public bool Died;
        }

        /// <summary>
        /// Walks a grid path to the monster of <see cref="_monsterMarker"/> until it is in sight and within
        /// <paramref name="approachCm"/>, re-planning when it moves (monster AI). Stops early when the player dies.
        /// </summary>
        private IEnumerator Approach(int approachCm, float seconds, ApproachResult result)
        {
            var marker = _monsterMarker!;
            var target = FixVec2.FromCm(marker.xCm, marker.yCm);
            var path = QaNavigation.FindPath(_grid, Own(), target)
                       ?? throw new QaFailure($"no walkable path from ({_tap.OwnXCm}, {_tap.OwnYCm}) to marker {marker.id}");
            float started = Time.realtimeSinceStartup;
            float progressAt = started;
            var progressFrom = Own();
            while (true)
            {
                if (OwnDead())
                {
                    Walk(null);
                    result.Died = true;
                    yield break;
                }
                if (_tap.Disconnected)
                    throw new QaFailure("disconnected on the way to the demon");
                var demon = NearestDemon(marker);
                bool seen = demon != null && QaWorldTap.TryGroundCm(demon, out int dx, out int dy) && Retarget(demon, dx, dy);
                result.DistanceCm = DistanceCm(Own(), target);
                if (seen && result.DistanceCm <= approachCm)
                    break;
                if (Time.realtimeSinceStartup - started > seconds)
                    throw new QaFailure($"did not reach the demon in {seconds:0} s (still {result.DistanceCm:0} cm away, demon in sight: {seen})");
                if (Time.realtimeSinceStartup - progressAt > QaTimeouts.StuckSeconds)
                {
                    if (DistanceCm(Own(), progressFrom) < QaTimeouts.StuckCm)
                        throw new QaFailure($"stuck at ({_tap.OwnXCm}, {_tap.OwnYCm}) for {QaTimeouts.StuckSeconds} s");
                    progressAt = Time.realtimeSinceStartup;
                    progressFrom = Own();
                }
                var waypoint = QaNavigation.NextWaypoint(_grid, Predicted(), path);
                Walk(waypoint);
                AimAt(waypoint);
                yield return null;
            }
            Walk(null);

            // The demon was seen at (x, y): it becomes the target, and the path is planned again when it moved away.
            bool Retarget(RemoteEntity seenDemon, int x, int y)
            {
                _demonId = seenDemon.Id;
                var seenAt = FixVec2.FromCm(x, y);
                if (DistanceCm(seenAt, target) > 100)
                {
                    target = seenAt;
                    path = QaNavigation.FindPath(_grid, Own(), target) ?? path;
                }
                return true;
            }
        }

        private static string Used(Dictionary<string, int> used) => string.Join(", ", used.Select(u => $"{u.Key} x{u.Value}"));

        /// <summary>Idle in the client's predicted action machine and <paramref name="action"/> off cooldown there.</summary>
        private static bool PredictedReady(PredictionDevClient dev, QaKitAction action)
        {
            var predictor = dev.Predictor;
            if (predictor == null || predictor.Actions.IsBusy)
                return false;
            var loadout = predictor.Loadout;
            int index = action.Slot == 0 ? loadout.AttackIndex : loadout.SlotIndex(action.Slot);
            return index >= 0 && predictor.Actions.CooldownTicks(index) == 0;
        }

        /// <summary>Walks straight away from <paramref name="demon"/> for a back-off (QaTimeouts), or until the player dies.</summary>
        private IEnumerator BackOff(FixVec2 demon)
        {
            var from = Own();
            double dx = QaNavigation.Cm(from.X) - QaNavigation.Cm(demon.X), dy = QaNavigation.Cm(from.Y) - QaNavigation.Cm(demon.Y);
            double length = Math.Max(1.0, Math.Sqrt(dx * dx + dy * dy));
            var away = FixVec2.FromCm((int)(QaNavigation.Cm(from.X) + dx / length * QaTimeouts.BackOffCm),
                (int)(QaNavigation.Cm(from.Y) + dy / length * QaTimeouts.BackOffCm));
            float started = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - started < QaTimeouts.BackOffSeconds && !OwnDead()
                   && DistanceCm(Own(), away) > QaTimeouts.StuckCm)
            {
                Walk(away);
                yield return null;
            }
            Walk(null);
        }

        /// <summary>Stand this close to the demon to attack it (a share of the normal attack's reach).</summary>
        private int ApproachCm() => BasicAttackReachCm() * QaTimeouts.ApproachPercentOfReach / 100;

        /// <summary>The player is dead: the own HP in the snapshots is 0 (it stays 0 until the respawn).</summary>
        private bool OwnDead() => _tap.Registry.HasOwnResources && _tap.Registry.OwnResources.Hp <= 0;

        private string OwnHp() => $"{_tap.Registry.OwnResources.Hp}/{_tap.Registry.OwnResources.MaxHp}";

        private bool KilledHitOn(uint target, int from)
        {
            for (int i = from; i < _tap.Hits.Count; i++)
            {
                if (_tap.Hits[i].Entry.TargetId == target && _tap.Hits[i].Entry.Kind == HitKind.Killed)
                    return true;
            }
            return false;
        }

        /// <summary>The newest hit that killed the player since <paramref name="from"/>, as text for the step.</summary>
        private string DescribeDeath(int from)
        {
            uint? own = _tap.Registry.OwnEntityId;
            for (int i = _tap.Hits.Count - 1; i >= from; i--)
            {
                var hit = _tap.Hits[i];
                if (own.HasValue && hit.Entry.TargetId == own.Value && hit.Entry.Kind == HitKind.Killed)
                {
                    string by = hit.Entry.AttackerId == _demonId ? " (the demon)" : "";
                    return $"killed at tick {hit.Tick} by entity {hit.Entry.AttackerId}{by} with {hit.Entry.Amount} damage{(hit.Entry.Crit ? " (crit)" : "")}";
                }
            }
            return $"HP 0 in the snapshot of tick {_tap.ServerTick} (no Killed hit message seen)";
        }

        /// <summary>The player's side of the fight: HP now and lowest, damage taken, deaths.</summary>
        private string OwnState(int from, int lowestHp, List<string> deaths)
        {
            var own = _tap.Registry.OwnResources;
            var taken = HitsOnOwn(from).ToList();
            bool low = own.Hp * 100 < own.MaxHp * QaTimeouts.LowHpPercent;
            return $"player HP {own.Hp}/{own.MaxHp}{(low ? " (low)" : "")}, lowest {lowestHp}, took {taken.Sum(h => h.Entry.Amount)} damage " +
                   $"in {taken.Count(h => h.Entry.Amount > 0)} hits ({taken.Count(h => h.Entry.Crit)} crits), {deaths.Count} deaths" +
                   (deaths.Count > 0 ? ": " + string.Join("; ", deaths) : "");
        }

        /// <summary>The player's HP and deaths in the step JSON (QA wave 3 F1: they were not recorded at all).</summary>
        private void AddOwnChecks(QaStepResult step, int from, int lowestHp, List<string> deaths)
        {
            var own = _tap.Registry.OwnResources;
            step.log.Add(OwnState(from, lowestHp, deaths));
            Check(step, "player deaths", deaths.Count < QaTimeouts.KillLives,
                $"fewer than {QaTimeouts.KillLives} (QaTimeouts.KillLives; respawn and fight again after each)",
                deaths.Count == 0 ? "0" : deaths.Count.ToString(CultureInfo.InvariantCulture) + ": " + string.Join("; ", deaths));
            Check(step, "player alive", own.Hp > 0, "HP > 0", $"HP {own.Hp}/{own.MaxHp}, lowest {lowestHp}");
        }

        private IEnumerable<QaHit> HitsOnOwn(int from)
        {
            uint? own = _tap.Registry.OwnEntityId;
            for (int i = from; i < _tap.Hits.Count; i++)
            {
                if (own.HasValue && _tap.Hits[i].Entry.TargetId == own.Value)
                    yield return _tap.Hits[i];
            }
        }

        /// <summary>
        /// How far the class's normal attack reaches from the attacker's center: its strike shapes (HitShapeDef), or its
        /// projectile's range (ProjectileDef), whichever is longer.
        /// </summary>
        private int BasicAttackReachCm()
        {
            string attack = _bindings!.NormalAttack;
            var effects = Rows<ActionEffectDef>().Where(e => e.ActionId == attack).ToList();
            var strikes = effects.Where(e => e.Kind == EffectKind.Strike).Select(e => e.Id).ToList();
            int reach = Rows<HitShapeDef>().Where(s => strikes.Contains(s.EffectId))
                .Select(s => s.ForwardOffsetCm + Math.Max(s.RangeCm, s.LengthCm + s.RadiusCm)).DefaultIfEmpty(0).Max();
            foreach (var shot in effects.Where(e => e.Kind == EffectKind.Projectile))
                reach = Math.Max(reach, ProjectileDef.Find(_config, shot.ProjectileId)?.RangeCm ?? 0);
            if (reach <= 0)
                throw new QaFailure($"the normal attack {attack} has no strike shape (HitShapeDef) and no projectile");
            return reach;
        }

        private IEnumerable<QaHit> OurHitsOn(uint target, int from)
        {
            uint? own = _tap.Registry.OwnEntityId;
            for (int i = from; i < _tap.Hits.Count; i++)
            {
                var hit = _tap.Hits[i];
                if (hit.Entry.TargetId == target && own.HasValue && hit.Entry.AttackerId == own.Value)
                    yield return hit;
            }
        }

        /// <summary>The server's position of the local player.</summary>
        private FixVec2 Own() => FixVec2.FromCm(_tap.OwnXCm, _tap.OwnYCm);

        /// <summary>The predicted position the player sees (the local view), for steering.</summary>
        private FixVec2 Predicted()
        {
            if (_views == null || _views.LocalView == null)
                return Own();
            var p = _views.LocalView.position;
            return FixVec2.FromCm(Mathf.RoundToInt(p.x * WorldUnits.CmPerUnit), Mathf.RoundToInt(p.z * WorldUnits.CmPerUnit));
        }

        /// <summary>Holds the screen-relative WASD keys toward <paramref name="target"/>, or releases them for null.</summary>
        private void Walk(FixVec2? target)
        {
            var keys = MoveKeys.None;
            if (target is FixVec2 to)
            {
                var from = Predicted();
                keys = QaNavigation.ScreenKeysToward(QaNavigation.Cm(to.X) - QaNavigation.Cm(from.X),
                    QaNavigation.Cm(to.Y) - QaNavigation.Cm(from.Y), _rig != null ? _rig.YawStep : 0);
            }
            _input!.Hold(_input.ButtonFor("MoveUp"), (keys & MoveKeys.W) != 0);
            _input.Hold(_input.ButtonFor("MoveLeft"), (keys & MoveKeys.A) != 0);
            _input.Hold(_input.ButtonFor("MoveDown"), (keys & MoveKeys.S) != 0);
            _input.Hold(_input.ButtonFor("MoveRight"), (keys & MoveKeys.D) != 0);
        }

        /// <summary>Puts the QA mouse over a ground point, at the character's height (what <c>GroundAim</c> reads back).</summary>
        private void AimAt(FixVec2 ground)
        {
            if (_rig == null || _views == null || _views.LocalView == null)
                return;
            var world = WorldUnits.FromGroundCm((float)QaNavigation.Cm(ground.X), (float)QaNavigation.Cm(ground.Y),
                _views.LocalView.position.y * WorldUnits.CmPerUnit);
            var screen = _rig.Camera.WorldToScreenPoint(world);
            _input!.Point(new Vector2(screen.x, screen.y));
        }

        private static double DistanceCm(FixVec2 a, FixVec2 b) =>
            QaNavigation.Distance(QaNavigation.Cm(a.X), QaNavigation.Cm(a.Y), QaNavigation.Cm(b.X), QaNavigation.Cm(b.Y));

        private IEnumerable<T> Rows<T>() =>
            _config.TryGet(typeof(T), out var table) ? table.Rows.Cast<T>() : Enumerable.Empty<T>();

        /// <summary>"Qa" and random letters (names allow ASCII letters only, CharacterCreationConfig).</summary>
        private static string NewName(int length)
        {
            var random = new System.Random();
            var letters = new char[Math.Max(length, 3)];
            letters[0] = 'Q';
            letters[1] = 'a';
            for (int i = 2; i < letters.Length; i++)
                letters[i] = (char)('a' + random.Next(26));
            return new string(letters);
        }

        private static string CommandLineValue(string option)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (string.Equals(args[i], option, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            }
            return "";
        }
    }
}
