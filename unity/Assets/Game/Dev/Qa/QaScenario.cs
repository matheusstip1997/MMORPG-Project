#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Game.Gameplay.Dev;
using Game.Gameplay.Entities;
using Game.Gameplay.Entities.Visuals;
using Game.Gameplay.Streaming;
using Game.UI.Config;
using Game.UI.HUD;
using Game.UI.ViewModels.Hud;
using Game.UI.ViewModels.Inventory;
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
    /// its echo, strafe and backpedal while facing one point, walk to the demon, lock it with the target lock (R-2.2-06),
    /// kill it with the class's kit (QaFightPlan), pick up its loot, move the stack in the
    /// inventory window (WP16), shoot the kit's first projectile at a wall (not applicable to a kit without one, such as
    /// the warrior's), wait for the respawn, and try the dev tools (spec 11.7). The class's kit is its <see cref="ActionBindings"/> row (backlog E). It drives the game only through <see cref="QaInput"/> (virtual keyboard and mouse),
    /// <see cref="QaUiPointer"/> (that mouse as UI Toolkit sees it) and <see cref="QaLoginDriver"/> (submit events on the
    /// login buttons), observes through <see cref="QaWorldTap"/>, and
    /// writes a screenshot and a JSON file per step under the run folder (<see cref="QaRunWriter"/>). Needs the Auth API
    /// (dev login) and the GameServer running; see <c>tools/qa/README.md</c>.
    /// </summary>
    public sealed class QaScenario : IDisposable
    {
        /// <summary>The login scene (the first scene of the build settings, made by <c>LoginSceneBuilder</c>).</summary>
        public const string LoginScene = "Assets/Game/UI/Login/Login.unity";

        public static readonly string[] StepNames =
        {
            "title", "dev-login", "server-select", "character", "enter-world", "chat-echo", "footwork", "walk-to-demon",
            "lock-demon", "kill-demon", "pick-up-drop", "move-stack", "arrow-wall", "wait-respawn", "dev-tools", "parry-boss",
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
        private readonly QaUiPointer _pointer = new QaUiPointer();
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
        private ItemDef? _pickedItem;

        /// <summary>Opening buffs walk-to-demon cast before the fight (not interrupted).</summary>
        private readonly HashSet<string> _castBeforeFight = new HashSet<string>();

        /// <summary>When the run last pressed an action that costs stamina (realtime seconds; below 0: never).</summary>
        private float _lastSpendAt = -1f;

        /// <summary>F3: kit actions that fired in this run (reached Active, not interrupted or refused), with how often.</summary>
        private readonly Dictionary<string, int> _fired = new Dictionary<string, int>();

        /// <summary>F3: the channeled action the HUD cast bar named in the <c>-cast</c> shot ("" until one was seen).</summary>
        private string _castBar = "";

        /// <summary>R-3.1-10: the normal attack chain's last hit in its Active phase, shot <c>-chain</c> ("" until seen).</summary>
        private string _chainShot = "";

        /// <summary>
        /// R-3.3-01 stagger marker, watched every frame of the fight (casts included) while set: the step the <c>-stagger</c>
        /// shot goes to; null outside the fight.
        /// </summary>
        private QaStepResult? _staggerStep;

        private bool _sawDemonStaggered, _sawStaggerMarker, _staggerMarkerEnded, _staggerShot;

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
                    Title, DevLogin, ServerSelect, Character, EnterWorld, ChatEcho, Footwork, WalkToDemon, LockDemon,
                    KillDemon, PickUpDrop, MoveStack, ArrowAtWall, WaitRespawn, TryDevTools, ParryBoss,
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

        /// <summary>
        /// Directional locomotion (R-2.2-01: WASD moves, the mouse faces): with the mouse held over a point screen-up of
        /// the player, strafe right (D) and backpedal (S), then walk back to the start (A, W). The strafe and backpedal legs
        /// check that the player moved its way and kept facing the aim, and take the shots <c>-strafe</c> and
        /// <c>-backpedal</c> at their end with the key still held.
        /// </summary>
        private IEnumerator Footwork(QaStepResult step)
        {
            int camera = _rig != null ? _rig.YawStep : 0;
            QaNavigation.ScreenDirection(0, camera, out double upX, out double upY);
            double legCm = _config.Single<StartingValuesM1>().MoveSpeedCmPerSec * (double)QaTimeouts.FootworkLegSeconds;
            for (int i = 0; i < QaTimeouts.SettleFrames; i++)
            {
                AimAlong(upX, upY);
                yield return null;
            }

            var legs = new (int Screen, string Button, string Shot)[]
            {
                (2, "MoveRight", "strafe"), (4, "MoveDown", "backpedal"), (6, "MoveLeft", ""), (0, "MoveUp", ""),
            };
            foreach (var leg in legs)
            {
                QaNavigation.ScreenDirection(leg.Screen, camera, out double dx, out double dy);
                var from = Predicted();
                double fromX = QaNavigation.Cm(from.X), fromY = QaNavigation.Cm(from.Y);
                var to = FixVec2.FromCm((int)Math.Round(fromX + dx * legCm), (int)Math.Round(fromY + dy * legCm));
                if (leg.Shot.Length > 0)
                    Check(step, leg.Shot + ": clear ground", QaNavigation.Clear(_grid, from, to), $"no wall within {legCm:0} cm", $"from ({fromX:0}, {fromY:0}) cm");
                var button = _input!.ButtonFor(leg.Button);
                _input.Hold(button, true);
                float started = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - started < QaTimeouts.FootworkLegSeconds)
                {
                    AimAlong(upX, upY);
                    yield return null;
                }
                if (leg.Shot.Length > 0)
                {
                    var end = Predicted();
                    double movedX = QaNavigation.Cm(end.X) - fromX, movedY = QaNavigation.Cm(end.Y) - fromY;
                    double moved = Math.Sqrt(movedX * movedX + movedY * movedY);
                    double minCm = legCm * QaTimeouts.FootworkPercentOfDistance / 100.0;
                    Check(step, leg.Shot + ": the player moved", moved >= minCm, $">= {minCm:0} cm", $"{moved:0} cm");
                    double off = AngleDeg(movedX, movedY, dx, dy);
                    Check(step, leg.Shot + ": along the key's direction", off <= QaTimeouts.FootworkToleranceDeg,
                        $"<= {QaTimeouts.FootworkToleranceDeg:0} degrees off", $"{off:0} degrees off");
                    var facing = _views!.LocalView!.forward;
                    double facingOff = AngleDeg(facing.x, facing.z, upX, upY);
                    Check(step, leg.Shot + ": facing the aim", facingOff <= QaTimeouts.FootworkToleranceDeg,
                        $"<= {QaTimeouts.FootworkToleranceDeg:0} degrees off screen-up", $"{facingOff:0} degrees off");
                    CheckFootworkAnimation(step, leg.Screen);
                    yield return Shot(step, leg.Shot);
                }
                _input.Hold(button, false);
            }
            Require(step);
            yield return Settle();
            yield return Shot(step);
        }

        /// <summary>
        /// The local visual's directional locomotion at the end of a footwork leg: a strafe (screen direction 2) turns the
        /// legs more than half the set's <c>legTurnMaxDeg</c> toward it, a backpedal (4) blends mostly the backward slots.
        /// </summary>
        private void CheckFootworkAnimation(QaStepResult step, int screen)
        {
            var visual = _views!.LocalVisual;
            var state = visual?.State;
            var set = visual?.Instance.GetComponent<EntityVisualRig>()?.AnimationSet;
            if (state == null || set == null)
            {
                Check(step, "local visual animated", false, "an animated local visual", "none (capsule or static prefab)");
                return;
            }
            step.log.Add($"locomotion: forward {state.ForwardCmPerSec:0} cm/s, right {state.RightCmPerSec:0} cm/s, leg turn {state.LegTurnDeg:0} deg, "
                + $"legs {(state.LegsBackward ? "backward" : "forward")}, directions F {state.Directions.Forward:0.00} R {state.Directions.Right:0.00} "
                + $"B {state.Directions.Backward:0.00} L {state.Directions.Left:0.00}");
            if (screen == 2)
            {
                float half = set.legTurnMaxDeg / 2f;
                Check(step, "strafe: the legs turn toward the movement", state.LegTurnDeg > half,
                    $"> {half:0} degrees (AnimationSet.legTurnMaxDeg / 2)", $"{state.LegTurnDeg:0} degrees");
            }
            else
            {
                Check(step, "backpedal: the backward loops play", state.Directions.Backward > 0.5f, "backward weight > 0.5",
                    state.Directions.Backward.ToString("0.00", CultureInfo.InvariantCulture));
            }
        }

        /// <summary>Holds the QA mouse <see cref="QaTimeouts.AimAheadCm"/> along a ground direction from the player.</summary>
        private void AimAlong(double dx, double dy)
        {
            var from = Predicted();
            AimAt(FixVec2.FromCm((int)Math.Round(QaNavigation.Cm(from.X) + dx * QaTimeouts.AimAheadCm),
                (int)Math.Round(QaNavigation.Cm(from.Y) + dy * QaTimeouts.AimAheadCm)));
        }

        /// <summary>The angle between two ground directions, in degrees (0 to 180).</summary>
        private static double AngleDeg(double ax, double ay, double bx, double by)
        {
            double lengths = Math.Sqrt(ax * ax + ay * ay) * Math.Sqrt(bx * bx + by * by);
            if (lengths < 1e-9)
                return 180.0;
            double cos = Math.Max(-1.0, Math.Min(1.0, (ax * bx + ay * by) / lengths));
            return Math.Acos(cos) * 180.0 / Math.PI;
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
            // H1: buff up before pulling, like a player: stop just outside the demon's aggro range and cast the kit's
            // opening buffs there (cast within reach, the demon's first pounce flinched them in Startup).
            var kit = QaFightPlan.Kit(_config, _bindings!);
            var buffs = kit.Where(a => a.OpensFight).ToList();
            // F3: the skills the fight never needs (Dash, Back Step, Simple Cleanse) are pressed here, outside aggro too.
            var tour = kit.Where(a => a.Moves || a.Utility).ToList();
            // P2 (first play session): the dodge is a 4 m move too (R-3.3-02, spec 12.7); its move is checked the same way.
            var dodge = QaFightPlan.Dodge(_config, _bindings!);
            if (dodge.Moves)
                tour.Add(dodge);
            else
                Check(step, "dodge moves the player", false, "a Dash effect on " + dodge.Id, "none");
            if (buffs.Count > 0 || tour.Count > 0)
            {
                int buffCm = MonsterDef.Find(_config, _monsterId)!.AggroRangeCm + QaTimeouts.BuffUpMarginCm;
                yield return Approach(buffCm, QaTimeouts.WalkSeconds, walk);
                if (walk.Died)
                    throw new QaFailure("the player died on the way to the demon: " + DescribeDeath(firstHit));
                yield return CastBuffs(buffs, step);
                yield return KitTour(tour, step);
            }
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
        /// The target lock (R-2.2-06): with the mouse 90 degrees around the player from the demon, the <c>targetLock</c> key
        /// locks the demon; the facing the client sends and the facing the server applied (own snapshot) turn to the demon,
        /// and its name plate shows the lock marker and its HP bar. A double press clears the lock and the mouse aims again.
        /// The fight after it runs without the lock, as before.
        /// </summary>
        private IEnumerator LockDemon(QaStepResult step)
        {
            var dev = UnityEngine.Object.FindAnyObjectByType<PredictionDevClient>()
                      ?? throw new QaFailure("no PredictionDevClient in the world scene");
            var feed = dev.Feed ?? throw new QaFailure("the world client has no feed");
            var row = Hotkeys.Find(_config, Hotkeys.TargetLock) ?? throw new QaFailure("no Hotkeys row " + Hotkeys.TargetLock);
            var key = _input!.ButtonForPath(row.Binding);
            Walk(null);
            AimAwayFromDemon(dev);
            yield return Settle();

            float lockPressedAt = Time.realtimeSinceStartup;
            yield return Press(key);
            yield return WaitUntil(() => feed.LockedTargetId != 0, QaTimeouts.UiSeconds, "a target lock after " + row.Binding);
            Check(step, row.Binding + " locks the demon", feed.LockedTargetId == _demonId, "entity " + _demonId, "entity " + feed.LockedTargetId);
            Require(step);

            double sentError = 180, serverError = 180;
            float started = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - started < QaTimeouts.LockSettleSeconds)
            {
                AimAwayFromDemon(dev); // the demon moves: the mouse keeps pointing beside it
                var own = dev.Predictor!.State.Position;
                sentError = FacingErrorToDemon(dev.Predictor.State.Facing, QaNavigation.Cm(own.X), QaNavigation.Cm(own.Y));
                serverError = FacingErrorToDemon(_tap.OwnFacing, _tap.OwnXCm, _tap.OwnYCm);
                if (sentError <= QaTimeouts.LockFacingToleranceDeg && serverError <= QaTimeouts.LockFacingToleranceDeg)
                    break;
                yield return null;
            }
            string tolerance = $"<= {QaTimeouts.LockFacingToleranceDeg:0} degrees off the demon (the mouse is {QaTimeouts.LockMouseOffsetDeg:0} off)";
            Check(step, "sent facing follows the lock", sentError <= QaTimeouts.LockFacingToleranceDeg, tolerance,
                sentError.ToString("0.0", CultureInfo.InvariantCulture) + " degrees");
            Check(step, "server facing follows the lock", serverError <= QaTimeouts.LockFacingToleranceDeg, tolerance,
                serverError.ToString("0.0", CultureInfo.InvariantCulture) + " degrees");
            yield return Settle();
            uint marked = _hud?.NamePlates?.VisibleLockMarkerEntity() ?? 0;
            Check(step, "HUD lock marker on the demon", marked == _demonId, "entity " + _demonId, "entity " + marked);
            int hpBars = _hud?.NamePlates?.VisibleHpBars() ?? 0;
            Check(step, "HUD HP bar of the locked demon", hpBars > 0, "> 0 shown", hpBars.ToString(CultureInfo.InvariantCulture));
            yield return Shot(step);

            // A double press: the first cycles (or keeps the only target), the second within the window clears the lock. It
            // starts a window after the lock press, else its first tap pairs with that press (the facing can settle at once).
            int windowMs = _config.Single<TargetLockSettings>().DoublePressWindowMs;
            yield return Wait(QaTimeouts.SecondsBeforeDoublePress(windowMs, Time.realtimeSinceStartup - lockPressedAt));
            yield return Press(key);
            yield return Press(key);
            yield return Settle();
            Check(step, "double press clears the lock", feed.LockedTargetId == 0, "no lock", "entity " + feed.LockedTargetId);
            marked = _hud?.NamePlates?.VisibleLockMarkerEntity() ?? 0;
            Check(step, "HUD lock marker gone", marked == 0, "none", "entity " + marked);

            double mouseError = 180;
            started = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - started < QaTimeouts.LockSettleSeconds)
            {
                AimAwayFromDemon(dev);
                var own = dev.Predictor!.State.Position;
                mouseError = Math.Abs(FacingErrorToDemon(dev.Predictor.State.Facing, QaNavigation.Cm(own.X), QaNavigation.Cm(own.Y))
                                      - QaTimeouts.LockMouseOffsetDeg);
                if (mouseError <= QaTimeouts.LockFacingToleranceDeg)
                    break;
                yield return null;
            }
            Check(step, "facing follows the mouse again", mouseError <= QaTimeouts.LockFacingToleranceDeg,
                $"{QaTimeouts.LockMouseOffsetDeg:0} degrees off the demon (+- {QaTimeouts.LockFacingToleranceDeg:0})",
                mouseError.ToString("0.0", CultureInfo.InvariantCulture) + " degrees from that");
        }

        /// <summary>Puts the QA mouse <see cref="QaTimeouts.LockMouseOffsetDeg"/> clockwise around the player from the demon.</summary>
        private void AimAwayFromDemon(PredictionDevClient dev)
        {
            if (dev.Predictor == null || !_tap.Registry.Entities.TryGetValue(_demonId, out var demon)
                || !QaWorldTap.TryGroundCm(demon, out int demonX, out int demonY))
                return;
            var own = dev.Predictor.State.Position;
            double x = QaNavigation.Cm(own.X), y = QaNavigation.Cm(own.Y);
            double away = Math.Atan2(demonX - x, demonY - y) + QaTimeouts.LockMouseOffsetDeg * Math.PI / 180.0; // clockwise from +Y
            const double reachCm = 300;
            AimAt(FixVec2.FromCm((int)Math.Round(x + Math.Sin(away) * reachCm), (int)Math.Round(y + Math.Cos(away) * reachCm)));
        }

        /// <summary>Degrees (0 to 180) between a binary-angle facing at (x, y) and the direction to the demon's newest position.</summary>
        private double FacingErrorToDemon(ushort facing, double xCm, double yCm)
        {
            if (!_tap.Registry.Entities.TryGetValue(_demonId, out var demon) || !QaWorldTap.TryGroundCm(demon, out int demonX, out int demonY))
                return 180;
            double toDemon = Math.Atan2(demonX - xCm, demonY - yCm) * 180.0 / Math.PI;
            double faced = facing * 360.0 / 65_536;
            double difference = Math.Abs(((faced - toDemon) % 360 + 540) % 360 - 180);
            return difference;
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
            // F2: below the heal line, the waits for a heal's stamina (no attacks meanwhile).
            var regen = QaStaminaRegen.From(_config);
            int staminaWaits = 0;
            string waitingFor = "";
            string closingFor = "";
            step.log.Add("kit: " + string.Join(", ", kit.Select(Describe)));
            // H2: back-offs stay out of the city (no-entry regions) and inside the demon's leash around its home.
            var noEntry = QaRegions.NoEntry(ClientWorld.Directory("m1"), _config);
            var home = FixVec2.FromCm(_monsterMarker!.xCm, _monsterMarker.yCm);
            int maxFromHomeCm = Math.Max(0, monster.LeashRangeCm - QaTimeouts.BackOffLeashMarginCm);
            // Smoke fix5c-4: back-off legs kited the fight toward the second demon's spawn, which killed the player after the kill.
            var avoid = OtherSpawnAggro();
            // H1: the self-buffs to cast as each life's fight starts, and the statuses the buff row must show once cast.
            var opening = new HashSet<string>(kit.Where(a => a.OpensFight && !_castBeforeFight.Contains(a.Id)).Select(a => a.Id));
            var buffsSeen = kit.Where(a => _castBeforeFight.Contains(a.Id)).ToDictionary(a => a.SelfStatusId, _ => false);
            bool buffShot = false;
            // H2: the demon's HP bucket went down in this life; back at full means it reset (leash or no-entry).
            bool demonDamaged = false;
            int firstHit = _tap.Hits.Count;
            byte firstBucket = _tap.Registry.Entities.TryGetValue(_demonId, out var demon) ? demon.HpBucket : (byte)0;
            byte lowestBucket = firstBucket;
            int lowestHp = _tap.Registry.OwnResources.Hp;
            var deaths = new List<string>();
            double lastSeenCm = double.MaxValue;
            int presses = 0;
            int longestChain = 0; // R-3.1-10: the most later hits one normal attack press ran
            bool fightShot = false;
            // Backlog I: the demon's name plate (UiStrings name) and its HP bar once damaged, and damage numbers.
            string monsterName = _login.Text.Get(UiStringCoverage.MonsterId(monster.Id));
            bool sawPlate = false, sawHpBar = false;
            long numbersBefore = _hud?.Models?.DamageNumbers.TotalSpawned ?? 0;
            long telegraphsBefore = _hud?.Models?.Telegraphs.TotalStarted ?? 0;
            _staggerStep = step;
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
                    // A new life is a fresh fight: the demon leashed home to full HP, and the buffs open it again.
                    demonDamaged = false;
                    opening.UnionWith(kit.Where(a => a.OpensFight).Select(a => a.Id));
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
                if (demon.HpBucket < Snapshot.TopHpBucket)
                    demonDamaged = true;
                else if (demonDamaged)
                    throw new QaFailure($"the demon reset to full HP (bucket {demon.HpBucket}) while the player lived; " +
                                        $"{backOffs} back-offs; " + OwnState(firstHit, lowestHp, deaths));
                sawPlate |= _hud?.NamePlates != null && _hud.NamePlates.VisibleWithName(monsterName) > 0;
                sawHpBar |= _hud?.NamePlates != null && _hud.NamePlates.VisibleHpBars() > 0;
                if (WatchStagger())
                    yield return Shot(step, "stagger");
                if (WatchBuffRow(buffsSeen) && !buffShot)
                {
                    buffShot = true;
                    yield return Shot(step, "buff");
                }
                if (!QaWorldTap.TryGroundCm(demon, out int x, out int y))
                {
                    yield return null;
                    continue;
                }
                var at = FixVec2.FromCm(x, y);
                lastSeenCm = DistanceCm(Own(), at);
                AimAt(at);
                for (; seenRefusals < _tap.Refusals.Count; seenRefusals++)
                {
                    var refusal = _tap.Refusals[seenRefusals];
                    var refused = kit.FirstOrDefault(a => a.Def.NetId == refusal.ActionNetId);
                    if (refused != null && refusal.Reason == ActionReason.OnCooldown)
                        heldUntil[refused.Id] = Time.realtimeSinceStartup + refused.Def.CooldownMs / 1000f;
                }
                var own = _tap.Registry.OwnResources;
                bool Ready(QaKitAction a) =>
                    PredictedReady(dev, a) && (!heldUntil.TryGetValue(a.Id, out float until) || Time.realtimeSinceStartup >= until);
                var choice = QaFightPlan.Choose(kit, new QaFightState(own.Hp, own.MaxHp, own.Mp, own.Stamina, lastSeenCm,
                    Ready, a => opening.Contains(a.Id), regen, SinceSpendMs(), a => !_fired.ContainsKey(a.Id)));
                if (choice.Move == QaFightMove.Wait && lastSeenCm > reachCm)
                {
                    Walk(at); // the demon moved (monster AI): follow it; anything in reach on the way goes first
                    yield return null;
                    continue;
                }
                if (choice.Move == QaFightMove.CloseIn)
                {
                    // F3: a skill not fired yet reaches less far than the fight is (Flamethrower, 350 cm): walk in for it.
                    if (closingFor != choice.Action!.Id)
                    {
                        closingFor = choice.Action.Id;
                        step.log.Add($"closing in for {closingFor} (reach {choice.Action.ReachCm} cm) from {lastSeenCm:0} cm");
                    }
                    Walk(at);
                    yield return null;
                    continue;
                }
                closingFor = "";
                Walk(null);
                if (choice.Move == QaFightMove.WaitForStamina)
                {
                    // F2: the heal is off cooldown and paid in MP; an attack now would spend its stamina and restart the delay.
                    if (waitingFor != choice.Action!.Id)
                    {
                        waitingFor = choice.Action.Id;
                        staminaWaits++;
                        step.log.Add($"HP {OwnHp()} below the heal line: no attacks for about {choice.WaitMs} ms, the stamina of " +
                                     $"{choice.Action.Id} (ST {own.Stamina}/{choice.Action.Def.StaminaCost}; staminaRegenPerSec " +
                                     $"{regen.PerSec}, staminaRegenDelayMs {regen.DelayMs})");
                    }
                    yield return null;
                    continue;
                }
                waitingFor = "";
                if (choice.Move == QaFightMove.Wait)
                {
                    yield return null;
                    continue;
                }
                if (choice.Move == QaFightMove.BackOff)
                {
                    backOffs++;
                    step.log.Add($"HP {OwnHp()} and no heal ready: backing off");
                    float backOffStarted = Time.realtimeSinceStartup;
                    var leg = new BackOffResult();
                    do
                    {
                        yield return BackOff(noEntry, home, maxFromHomeCm, QaTimeouts.BackOffCm, avoid, leg);
                        if (!leg.Found)
                        {
                            step.log.Add($"no back-off direction out of the city and inside the leash from ({_tap.OwnXCm}, {_tap.OwnYCm}); fighting on");
                            break;
                        }
                        own = _tap.Registry.OwnResources;
                    } while (!OwnDead() && Time.realtimeSinceStartup - backOffStarted < QaTimeouts.BackOffRecoverSeconds
                             && !QaFightPlan.Recovered(kit, new QaFightState(own.Hp, own.MaxHp, own.Mp, own.Stamina, lastSeenCm, Ready)));
                    step.log.Add($"back-off over after {Time.realtimeSinceStartup - backOffStarted:0.0} s with HP {OwnHp()}");
                    continue;
                }
                var action = choice.Action!;
                if (choice.Move == QaFightMove.StepBack)
                {
                    var room = new StepBackResult();
                    yield return StepBack(action, noEntry, home, maxFromHomeCm, avoid, room);
                    if (!room.InReach)
                    {
                        // No room this time: leave it until its cooldown would have passed, then try again.
                        heldUntil[action.Id] = Time.realtimeSinceStartup + action.Def.CooldownMs / 1000f;
                        step.log.Add($"no room for {action.Id} (needs {action.MinReachCm}-{action.ReachCm} cm, got {room.DistanceCm:0} cm)");
                        continue;
                    }
                    step.log.Add($"{action.Id} from {room.DistanceCm:0} cm");
                }
                used[action.Id] = used.TryGetValue(action.Id, out int count) ? count + 1 : 1;
                opening.Remove(action.Id);
                // Until the predicted machine is idle again: a refused or interrupted action frees the player at once.
                var cast = new CastResult();
                yield return Cast(step, action, dev, cast);
                presses++;
                longestChain = Math.Max(longestChain, cast.ChainHits.Count);
                if (action.SelfStatusId.Length > 0)
                {
                    if (cast.Interrupted)
                    {
                        // The demon's hit cancelled it before its effect (spec 3.1): no status to show; cast it again when ready.
                        step.log.Add($"{action.Id} was interrupted before its effect; casting it again when ready");
                        if (action.OpensFight)
                            opening.Add(action.Id);
                    }
                    else if (!buffsSeen.ContainsKey(action.SelfStatusId))
                    {
                        buffsSeen[action.SelfStatusId] = false;
                    }
                }
                if (!fightShot && OurHitsOn(_demonId, firstHit).Any())
                {
                    fightShot = true;
                    yield return Shot(step, "fight");
                }
            }
            Walk(null);
            _staggerStep = null;
            var hits = OurHitsOn(_demonId, firstHit).ToList();
            int damage = hits.Sum(hit => hit.Entry.Amount);
            var despawn = _tap.Events.LastOrDefault(e => !e.Spawned && e.EntityId == _demonId);
            _deathTick = despawn.EntityId == _demonId ? despawn.Tick : _tap.ServerTick;
            step.log.Add($"{presses} actions, {hits.Count} hits, {damage} damage, death at tick {_deathTick}");
            step.log.Add($"used: {Used(used)}; {backOffs} back-offs; {staminaWaits} waits for a heal's stamina");

            // F3: every action of the kit fires at least once in a run. One the fight did not need (a heal at high HP) is
            // pressed now, with the demon dead.
            foreach (var leftover in kit.Where(a => !_fired.ContainsKey(a.Id)).ToList())
            {
                var meters = _tap.Registry.OwnResources;
                int waitMs = Math.Max(regen.MsUntil(meters.Stamina, leftover.ChainStaminaCost, SinceSpendMs()), leftover.Def.CooldownMs);
                float waitFrom = Time.realtimeSinceStartup;
                while (Alive("waiting to press " + leftover.Id) && !UsableNow(leftover, dev)
                       && Time.realtimeSinceStartup - waitFrom < waitMs / 1000f + QaTimeouts.UiSeconds)
                    yield return null;
                var cast = new CastResult();
                yield return Cast(step, leftover, dev, cast);
                step.log.Add($"{leftover.Id} pressed after the kill (not needed in the fight): {cast}");
                if (cast.Fired && leftover.SelfStatusId.Length > 0 && !buffsSeen.ContainsKey(leftover.SelfStatusId))
                    buffsSeen[leftover.SelfStatusId] = false;
            }
            Check(step, "every kit action fired", kit.All(a => _fired.ContainsKey(a.Id)),
                "each of " + string.Join(", ", kit.Select(a => a.Id)) + " at least once (QA wave 6 F3)",
                string.Join(", ", kit.Select(a => $"{a.Id} x{(_fired.TryGetValue(a.Id, out int n) ? n : 0)}")));
            if (kit.Any(a => a.Def.ChannelMs > 0))
                Check(step, "HUD cast bar during a channel", _castBar.Length > 0, "the cast bar names a channeled action (shot -cast)",
                    _castBar.Length > 0 ? _castBar : "not seen");
            else
                step.log.Add("no channeled action in this kit: no cast bar to check");
            var attack = kit.First(a => a.Slot == 0);
            if (attack.Chain.Count > 0)
                Check(step, "attack chain reached its last hit", _chainShot.Length > 0,
                    $"{attack.Chain.Count + 1} hits, {attack.Chain[attack.Chain.Count - 1].Id} active (R-3.1-10, shot -chain)",
                    _chainShot.Length > 0 ? _chainShot : $"longest chain {longestChain + 1} hits");
            Check(step, "hit messages on the demon", hits.Count > 0, "> 0 hits from us", hits.Count.ToString(CultureInfo.InvariantCulture));
            Check(step, "HP bucket went down", lowestBucket < firstBucket, "below " + firstBucket, lowestBucket.ToString(CultureInfo.InvariantCulture));
            Check(step, "killing hit", hits.Any(hit => hit.Entry.Kind == HitKind.Killed), "a Killed hit", string.Join(",", hits.Select(hit => hit.Entry.Kind).Distinct()));
            Check(step, "damage covers max HP", damage >= monster.MaxHp, ">= " + monster.MaxHp + " (MonsterDef)", damage.ToString(CultureInfo.InvariantCulture));
            long numbers = (_hud?.Models?.DamageNumbers.TotalSpawned ?? 0) - numbersBefore;
            Check(step, "HUD damage numbers", numbers >= hits.Count, ">= " + hits.Count + " (our hits)", numbers.ToString(CultureInfo.InvariantCulture));
            Check(step, "HUD name plate", sawPlate, "'" + monsterName + "' above the demon", sawPlate ? "shown" : "not shown");
            Check(step, "HUD monster HP bar", sawHpBar, "shown once damaged", sawHpBar ? "shown" : "not shown");
            if (_sawDemonStaggered)
            {
                // R-3.3-01, spec 9.3 stagger_marker: the poise break the snapshots showed is marked on the plate, then cleared.
                Check(step, "HUD stagger marker on the staggered demon", _sawStaggerMarker, "burst and 'Staggered' over its plate (shot -stagger)",
                    _sawStaggerMarker ? "shown" : "not shown");
                Check(step, "HUD stagger marker gone after the stagger", _staggerMarkerEnded, "hidden again while the demon lives",
                    _staggerMarkerEnded ? "hidden" : "not seen hidden before the kill");
            }
            else
            {
                step.log.Add("the demon was never staggered in this fight: no stagger marker to check");
                Check(step, "HUD stagger marker only on a staggered demon", !_sawStaggerMarker, "never shown", _sawStaggerMarker ? "shown" : "never shown");
            }
            step.log.Add($"HUD: {numbers} damage numbers, {(_hud?.Models?.Telegraphs.TotalStarted ?? 0) - telegraphsBefore} monster telegraphs started");
            // H1: every status the kit cast on the player shows in the buff row (spec 9.3 buff_row, the status name while
            // icons are placeholders). A buff cast just before the kill gets a moment to arrive.
            float buffWait = Time.realtimeSinceStartup;
            while (buffsSeen.ContainsValue(false) && Time.realtimeSinceStartup - buffWait < QaTimeouts.BuffRowSeconds)
            {
                WatchBuffRow(buffsSeen);
                yield return null;
            }
            foreach (var buff in buffsSeen)
                Check(step, "buff row shows " + buff.Key, buff.Value, "'" + _login.Text.Get(UiStringCoverage.StatusId(buff.Key)) + "' chip",
                    buff.Value ? "shown" : "not shown (chips: " + string.Join(", ", _hud?.BuffRow?.ChipTexts() ?? Array.Empty<string>()) + ")");
            var notCast = kit.Where(a => a.SelfStatusId.Length > 0 && !buffsSeen.ContainsKey(a.SelfStatusId)).Select(a => a.Id).ToList();
            if (notCast.Count > 0)
                step.log.Add("self statuses not cast in this fight: " + string.Join(", ", notCast));
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
            yield return WaitUntil(() => Alive("waiting for the drop") && (drop = NearestDrop()) != null, QaTimeouts.LootSeconds, "the demon's drop");
            var item = Rows<ItemDef>().FirstOrDefault(row => row.NetId == drop!.DefId);
            Check(step, "drop names an item", item != null, "an ItemDef net id", drop!.DefId.ToString(CultureInfo.InvariantCulture));
            Require(step);
            QaWorldTap.TryGroundCm(drop, out int dropX, out int dropY);
            var at = FixVec2.FromCm(dropX, dropY);
            int rangeCm = _config.Single<LootSettings>().PickupRangeCm;
            int nearCm = rangeCm * QaTimeouts.LootApproachPercentOfRange / 100;
            step.log.Add($"drop {drop.Id} ({item!.Id}) at ({dropX}, {dropY}); pickup range {rangeCm} cm, stopping within {nearCm} cm");
            float started = Time.realtimeSinceStartup;
            while (Alive("walking to the drop") && DistanceCm(Predicted(), at) > nearCm)
            {
                if (Time.realtimeSinceStartup - started > QaTimeouts.LootSeconds)
                    throw new QaFailure($"did not reach the drop in {QaTimeouts.LootSeconds:0} s");
                Walk(at);
                yield return null;
            }
            Walk(null);
            yield return Wait(3 * (float)_tap.TickMs / 1000f); // the server position catches up with the prediction
            // Backlog G: the HUD's pickup prompt stands at this drop and names the pickup key before it is pressed (spec 9.3).
            var prompt = _hud?.PickupPrompt;
            var feed = UnityEngine.Object.FindAnyObjectByType<PredictionDevClient>()?.Feed;
            float promptFrom = Time.realtimeSinceStartup;
            while ((prompt == null || prompt.DropId != drop.Id) && Time.realtimeSinceStartup - promptFrom < QaTimeouts.LootSeconds)
                yield return null;
            string promptText = _login.Text.Get(HudTextIds.PickupPrompt, new TextArg("key", feed?.PickupKeyName ?? ""));
            Check(step, "pickup prompt at the drop", prompt != null && prompt.DropId == drop.Id && prompt.Text == promptText
                && feed!.PickupKeyName.Length > 0, $"\"{promptText}\" at drop {drop.Id}",
                prompt == null ? "no HUD prompt" : $"\"{prompt.Text}\" at drop {prompt.DropId}");
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
            // The world client drops the drop at its leave tick on the render clock, a little after this tap.
            promptFrom = Time.realtimeSinceStartup;
            while (prompt != null && prompt.DropId == drop.Id && Time.realtimeSinceStartup - promptFrom < QaTimeouts.LootSeconds)
                yield return null;
            Check(step, "pickup prompt gone with the drop", prompt == null || prompt.DropId != drop.Id, "not at the taken drop",
                prompt == null ? "no HUD prompt" : $"at drop {prompt.DropId}");
            _pickedItem = item;
            yield return Settle();
            yield return Shot(step);
        }

        /// <summary>
        /// WP16 inventory window: open it with its <c>Hotkeys</c> key, hover the stack picked up in the step before (tooltip),
        /// click it and then the last empty slot (click-to-move), and check that the server confirms the move and sends the
        /// bag as saved with the stack in its new slot; then close the window. <c>Run-QaSmoke.ps1</c> checks the database
        /// after the run (<see cref="QaRunResult.bagItemId"/>). QA wave 6 F1: the pointer reaches the window through UI
        /// Toolkit's own dispatch (<see cref="QaUiPointer"/>; the QA mouse follows it for the game), so hovering, click-to-move,
        /// the drag and the masking of attack and parry run the window's real handlers; a drag out of the window is refused
        /// with its toast (spec 9.13).
        /// </summary>
        private IEnumerator MoveStack(QaStepResult step)
        {
            var window = _hud?.InventoryWindow;
            var model = _hud?.InventoryModel;
            var feed = _hud?.InventoryBinder?.Feed;
            var dev = UnityEngine.Object.FindAnyObjectByType<PredictionDevClient>();
            Check(step, "inventory window built", window != null && model != null && feed != null && dev != null, "HudRoot.InventoryWindow",
                window != null ? "built" : "missing");
            Require(step);
            var item = _pickedItem ?? throw new QaFailure("no item was picked up in the step before");
            var bag = _tap.Inventory ?? throw new QaFailure("no saved bag yet");
            var source = bag.Slots.First(s => s.ItemNetId == item.NetId);
            var empty = Enumerable.Range(0, bag.SlotCount).Reverse().Where(i => bag.Slots.All(s => s.Slot != i)).ToList();
            if (empty.Count < 2)
                throw new QaFailure($"the bag needs two empty slots for the click and the drag ({empty.Count} empty)");
            int clickTo = empty[0], dragTo = empty[1];
            string itemName = _login.Text.Get(item.NameId);
            step.log.Add($"{item.Id} x{source.Count} in slot {source.Slot}; click-to-move to slot {clickTo}, then drag to slot {dragTo}");

            var toggle = Hotkeys.Find(_config, Hotkeys.ToggleInventory) ?? throw new QaFailure("no Hotkeys row toggleInventory");
            var key = _input!.ButtonForPath(toggle.Binding);
            yield return Press(key);
            yield return WaitUntil(() => model!.Open && window!.IsShown, QaTimeouts.UiSeconds, "the inventory window to open");
            yield return Settle();
            Check(step, "window opens with its key", model!.Open, toggle.Binding, model.Open ? "open" : "closed");
            var shown = model.Slot(source.Slot);
            Check(step, "window shows the saved stack", shown.ItemNetId == item.NetId && shown.Count == source.Count && !shown.Preview,
                $"{item.Id} x{source.Count} in slot {source.Slot}", $"#{shown.ItemNetId} x{shown.Count}{(shown.Preview ? " (preview)" : "")}");
            Check(step, "slots from ContainerDef", window!.SlotElementCount == bag.SlotCount, bag.SlotCount.ToString(CultureInfo.InvariantCulture),
                window.SlotElementCount.ToString(CultureInfo.InvariantCulture));
            var panel = window.Window.panel ?? throw new QaFailure("the inventory window is on no panel");

            // Hover: the window's own enter and leave handlers set the hovered slot and the pointer mask.
            yield return PointerTo(panel, QaUiPointer.Center(window.SlotElement(source.Slot)), QaTimeouts.PointerSteps);
            yield return Settle();
            Check(step, "UI Toolkit sees the pointer (hover)", model.Hovered == source.Slot, "slot " + source.Slot + " hovered",
                model.Hovered == InventoryWindowViewModel.None ? "nothing hovered" : "slot " + model.Hovered);
            Check(step, "tooltip names the item", window.TooltipText.StartsWith(itemName, StringComparison.Ordinal),
                itemName + " ...", window.TooltipText.Replace('\n', '|'));
            Check(step, "pointer on the window masks the mouse buttons", feed!.UiCapturesPointer, "WorldClientFeed.UiCapturesPointer",
                feed.UiCapturesPointer ? "set" : "not set");
            yield return Shot(step, "open");

            // Spec 9.13: while the pointer is on the window its buttons are not attack or parry.
            var mask = new MaskWatch();
            var attack = ActionDef.Find(_config, _bindings!.NormalAttack)!;
            float holdSeconds = attack.TotalMs() / 1000f + QaTimeouts.PressSeconds;
            yield return HoldOverWindow(_input.ButtonFor("Attack"), holdSeconds, dev!, mask);
            yield return HoldOverWindow(_input.ButtonFor("Parry"), holdSeconds, dev!, mask);
            Check(step, "attack and parry held over the window start nothing", mask.Clean, "predicted machine idle, stamina not spent",
                mask.Describe());
            Require(step);

            // Click-to-move: a click on the stack selects it, a click on an empty slot moves it there.
            int answers = _tap.MoveResults.Count;
            yield return PointerClick(panel, dev!, mask);
            Check(step, "click selects the stack", model.Selected == source.Slot, "slot " + source.Slot + " selected",
                model.Selected == InventoryWindowViewModel.None ? "nothing selected" : "slot " + model.Selected);
            Require(step);
            yield return PointerTo(panel, QaUiPointer.Center(window.SlotElement(clickTo)), QaTimeouts.PointerSteps);
            yield return PointerClick(panel, dev!, mask);
            var preview = model.Slot(clickTo);
            Check(step, "preview shows the click move at once", preview.ItemNetId == item.NetId,
                $"{item.Id} in slot {clickTo}", $"#{preview.ItemNetId}{(preview.Preview ? " (preview)" : "")}");
            yield return ConfirmMove(step, answers, item, source.Count, source.Slot, clickTo);

            // Drag: press on the stack, move past InventoryWindowSettings.dragThresholdPx onto another empty slot, release.
            answers = _tap.MoveResults.Count;
            yield return PointerDown(panel, dev!, mask);
            yield return PointerTo(panel, QaUiPointer.Center(window.SlotElement(dragTo)), QaTimeouts.PointerSteps, dev, mask);
            yield return Settle();
            var ghost = panel.visualTree.Q<VisualElement>(GhostName);
            bool dragging = ghost != null && ghost.style.display == DisplayStyle.Flex;
            Check(step, "drag shows the stack under the pointer", dragging, "the drag ghost (" + GhostName + ") over slot " + dragTo,
                dragging ? "shown" : "not shown");
            yield return Shot(step, "drag"); // the attack button is held all along: the mask keeps it from attacking
            yield return PointerUp(panel);
            var dropped = model.Slot(dragTo);
            Check(step, "preview shows the drag move at once", dropped.ItemNetId == item.NetId,
                $"{item.Id} in slot {dragTo}", $"#{dropped.ItemNetId}{(dropped.Preview ? " (preview)" : "")}");
            yield return ConfirmMove(step, answers, item, source.Count, clickTo, dragTo);
            Check(step, "attack held through the clicks and the drag starts nothing", mask.Clean,
                "predicted machine idle, stamina not spent", mask.Describe());
            _writer.Result.bagItemId = item.Id;
            _writer.Result.bagSlot = dragTo;
            _writer.Result.bagFromSlot = clickTo;
            _writer.Result.bagCount = source.Count;
            yield return Shot(step, "moved");

            // A refused move (spec 9.13): a stack dragged out of the window goes nowhere, and the toast gives the reason text.
            answers = _tap.MoveResults.Count;
            int sent = model.Pending.Count;
            var outside = new Vector2(window.Window.worldBound.xMin - QaTimeouts.DropOutsidePx, window.Window.worldBound.center.y);
            yield return PointerDown(panel, dev!, mask);
            yield return PointerTo(panel, outside, QaTimeouts.PointerSteps, dev, mask);
            yield return PointerUp(panel);
            yield return Settle();
            var toast = _hud!.Models!.Toast;
            string reason = ClientReasonCode.ItemDropUnavailable.ToString();
            Check(step, "drag out of the window is refused with its toast", toast.Visible && toast.ReasonCode == reason,
                $"'{_login.Reasons.Text(reason)}' ({reason})", toast.Visible ? $"'{_login.Reasons.Text(toast.ReasonCode)}' ({toast.ReasonCode})" : "no toast");
            yield return Shot(step, "refused");
            yield return Wait(QaTimeouts.RefusedMoveSeconds);
            var stays = model.Slot(dragTo);
            Check(step, "refused move sends nothing and the stack stays", _tap.MoveResults.Count == answers && model.Pending.Count == sent
                    && stays.ItemNetId == item.NetId && !stays.Preview,
                $"no request, {item.Id} still in slot {dragTo}",
                $"{_tap.MoveResults.Count - answers} answers, #{stays.ItemNetId} in slot {dragTo}");
            // Merging two stacks is not reachable in M1 play: a pickup always fills the stack of the same item first, every
            // M1 drop is common and split is "Not yet" (spec 9.13), so the bag never holds two stacks of one item below max.
            // A live merge check is not part of the M1 harness (user decision 2026-10-26; it would need a dev give-item
            // command or a seeded bag).
            step.log.Add("merge not run live (not part of the M1 harness): the bag cannot hold two stacks of the same item in " +
                         "M1 play (pickups fill the existing stack, no split); covered by InventoryWindowTests and InventoryMoveWorldTests");

            yield return PointerTo(panel, QaUiPointer.Center(window.SlotElement(dragTo)), QaTimeouts.PointerSteps);
            yield return Press(key);
            yield return WaitUntil(() => !model.Open, QaTimeouts.UiSeconds, "the inventory window to close");
            Check(step, "window closes with its key", !window.IsShown, "closed", window.IsShown ? "open" : "closed");
            yield return PointerTo(panel, panel.visualTree.worldBound.center, 1);
            yield return Settle();
            Check(step, "mask released once the window is closed", !feed.UiCapturesPointer, "UiCapturesPointer cleared",
                feed.UiCapturesPointer ? "still set" : "cleared");
        }

        private const string GhostName = "inv_drag_ghost";

        /// <summary>Waits for the answer to the move just sent and the saved bag that shows it, and checks both.</summary>
        private IEnumerator ConfirmMove(QaStepResult step, int answers, ItemDef item, int count, int from, int to)
        {
            yield return WaitUntil(() => _tap.MoveResults.Count > answers, QaTimeouts.LootSeconds, $"the answer to the move {from} -> {to}");
            var result = _tap.MoveResults[answers];
            Check(step, $"move {from} -> {to} accepted", result.Reason == InventoryMoveReason.None && result.Outcome == InventoryMoveOutcome.Moved,
                nameof(InventoryMoveOutcome.Moved), result.Reason == InventoryMoveReason.None ? result.Outcome.ToString() : result.Reason.ToString());
            Require(step);
            yield return WaitUntil(() => _tap.Inventory!.Slots.Any(s => s.Slot == to && s.ItemNetId == item.NetId),
                QaTimeouts.LootSeconds, $"the saved bag with the stack in slot {to}");
            var moved = _tap.Inventory!.Slots.First(s => s.Slot == to);
            Check(step, $"saved bag: the stack in slot {to}", moved.Count == count && _tap.Inventory.Slots.All(s => s.Slot != from),
                $"{item.Id} x{count} in slot {to}, slot {from} empty", $"x{moved.Count} in slot {to}");
            yield return Settle();
            var model = _hud!.InventoryModel!;
            var confirmed = model.Slot(to);
            Check(step, $"window shows slot {to} confirmed", confirmed.ItemNetId == item.NetId && !confirmed.Preview && model.Slot(from).IsEmpty,
                "confirmed, not a preview", confirmed.Preview ? "preview" : "confirmed");
        }

        /// <summary>Anything the masked mouse buttons started while the pointer was on the window (spec 9.13).</summary>
        private sealed class MaskWatch
        {
            public int BusyFrames;
            public int LowestStamina = int.MaxValue;
            public int StartStamina = -1;

            public bool Clean => BusyFrames == 0 && (StartStamina < 0 || LowestStamina >= StartStamina);

            public string Describe() => $"{BusyFrames} frames with an action running, stamina {StartStamina} -> lowest " +
                                        (LowestStamina == int.MaxValue ? "n/a" : LowestStamina.ToString(CultureInfo.InvariantCulture));
        }

        private void Watch(PredictionDevClient? dev, MaskWatch? mask)
        {
            if (dev == null || mask == null)
                return;
            int stamina = _tap.Registry.OwnResources.Stamina;
            if (mask.StartStamina < 0)
                mask.StartStamina = stamina;
            mask.LowestStamina = Math.Min(mask.LowestStamina, stamina);
            if (dev.Predictor != null && dev.Predictor.Actions.IsBusy)
                mask.BusyFrames++;
        }

        /// <summary>Holds a QA mouse button for <paramref name="seconds"/> while the pointer stays on the window.</summary>
        private IEnumerator HoldOverWindow(ButtonControl button, float seconds, PredictionDevClient dev, MaskWatch mask)
        {
            Watch(dev, mask);
            _input!.Hold(button, true);
            float started = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - started < seconds)
            {
                Watch(dev, mask);
                yield return null;
            }
            _input.Hold(button, false);
            for (int i = 0; i < QaTimeouts.SettleFrames; i++)
            {
                Watch(dev, mask);
                yield return null;
            }
        }

        /// <summary>
        /// Moves the pointer to <paramref name="at"/> (panel coordinates) in <paramref name="steps"/> frames: UI Toolkit sees it
        /// through <see cref="QaUiPointer"/>, the game through the QA mouse at the same place on the screen.
        /// </summary>
        private IEnumerator PointerTo(IPanel panel, Vector2 at, int steps, PredictionDevClient? dev = null, MaskWatch? mask = null)
        {
            var from = _pointer.Position.x < 0f ? at : _pointer.Position;
            for (int i = 1; i <= Math.Max(1, steps); i++)
            {
                var p = Vector2.Lerp(from, at, i / (float)Math.Max(1, steps));
                _pointer.Move(panel, p);
                _input!.Point(ScreenPoint(panel, p));
                Watch(dev, mask);
                yield return null;
            }
        }

        /// <summary>A left press like a player's: the QA mouse button for the game, a pointer down for UI Toolkit.</summary>
        private IEnumerator PointerDown(IPanel panel, PredictionDevClient dev, MaskWatch mask)
        {
            _input!.Hold(_input.Mouse.leftButton, true);
            _pointer.Down(panel);
            for (int i = 0; i < QaTimeouts.SettleFrames; i++)
            {
                Watch(dev, mask);
                yield return null;
            }
        }

        /// <summary>Releases the left button: the QA mouse first, so no tick samples it held after the window lets go.</summary>
        private IEnumerator PointerUp(IPanel panel)
        {
            _input!.Hold(_input.Mouse.leftButton, false);
            yield return null;
            _pointer.Up(panel);
            yield return null;
        }

        private IEnumerator PointerClick(IPanel panel, PredictionDevClient dev, MaskWatch mask)
        {
            yield return PointerDown(panel, dev, mask);
            yield return PointerUp(panel);
            yield return Settle();
        }

        /// <summary>
        /// The screen pixel (bottom-left origin, like <c>Pointer.position</c>) of a panel position: scaled from the panel to the
        /// screen (the HUD panel scales with the screen height, spec 9.7).
        /// </summary>
        private static Vector2 ScreenPoint(IPanel panel, Vector2 at)
        {
            var root = panel.visualTree.worldBound;
            float sx = Screen.width / Mathf.Max(1f, root.width), sy = Screen.height / Mathf.Max(1f, root.height);
            return new Vector2(at.x * sx, Screen.height - at.y * sy);
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
            yield return WaitUntil(() => Alive("waiting for the demon's respawn") && TryFindRespawn(marker, out respawn),
                Math.Max(timeout - QaTimeouts.MidWaitShotSeconds, 1f), "the demon's respawn");
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

        /// <summary>
        /// Spec 11.7 (backlog J, A): the dev tools of the dev scene. Shows the hitbox gizmos, the action timeline and the tick
        /// panel with their Hotkeys rows, attacks once, and checks that each has data: shapes from prediction and from the
        /// server's strike trace (when the attack strikes), timeline samples from both sides, server tick times. Then, when
        /// the run has its own tables copy (<c>MMORPG_DATA_DIR</c>, set by Run-QaSmoke.ps1), changes the attack's recovery
        /// there and waits until the server accepted the reload and the client applied the same tables; it puts the file
        /// back the same way. Never edits the repository's tables.
        /// </summary>
        private IEnumerator TryDevTools(QaStepResult step)
        {
            var client = UnityEngine.Object.FindAnyObjectByType<PredictionDevClient>();
            var gizmos = UnityEngine.Object.FindAnyObjectByType<HitboxGizmoView>();
            var timeline = UnityEngine.Object.FindAnyObjectByType<ActionTimelineView>();
            var ticks = UnityEngine.Object.FindAnyObjectByType<TickTimingView>();
            var reload = UnityEngine.Object.FindAnyObjectByType<DevTableReload>();
            if (client == null || gizmos == null || timeline == null || ticks == null || reload == null)
                throw new QaFailure("the dev tools are missing from the world scene (rebuild it with DevSceneBuilder)");
            foreach (var id in new[] { Hotkeys.DevHitboxes, Hotkeys.DevTimeline, Hotkeys.DevTickTiming })
                yield return Press(_input!.ButtonForPath(Hotkeys.Find(_config, id)!.Binding));
            yield return Settle();
            Check(step, "views shown", gizmos.Shown && timeline.Shown && ticks.Shown, "F4, F5, F6 shown",
                $"{gizmos.Shown}, {timeline.Shown}, {ticks.Shown}");

            var attack = ActionDef.Find(_config, _bindings!.NormalAttack)!;
            bool strikes = Rows<ActionEffectDef>().Any(e => e.ActionId == attack.Id && e.Kind == EffectKind.Strike);
            yield return WaitUntil(() => _tap.Registry.OwnResources.Stamina >= attack.StaminaCost, QaTimeouts.DevToolsSeconds, "stamina for the attack");
            long predicted = gizmos.PredictedShapes, server = gizmos.ServerShapes;
            yield return Press(_input!.ButtonFor("Attack"));
            if (strikes)
            {
                yield return WaitUntil(() => gizmos.PredictedShapes > predicted && gizmos.ServerShapes > server,
                    QaTimeouts.DevToolsSeconds, "predicted and server hit shapes (MMORPG_DEV_TOOLS=1 on the server)");
                Check(step, "hit shapes drawn", true, "predicted and server shapes of " + attack.Id,
                    $"{gizmos.PredictedShapes - predicted} predicted, {gizmos.ServerShapes - server} server");
                yield return Settle();
                yield return Shot(step, "hitboxes");
            }
            else
            {
                step.log.Add($"{attack.Id} strikes nothing (projectile kit): no strike shapes to check");
            }
            yield return WaitUntil(() => gizmos.LinesDrawn > 0, QaTimeouts.DevToolsSeconds, "gizmo lines (body circles)");
            yield return WaitUntil(() => timeline.Model!.PredictedCount > 0 && timeline.Model.ServerCount > 0,
                QaTimeouts.DevToolsSeconds, "timeline samples from prediction and the server");
            yield return WaitUntil(() => ticks.Ticks > 0, QaTimeouts.DevToolsSeconds, "server tick times from telemetry");
            var summary = ticks.Summary;
            Check(step, "timeline recorded", true, "samples on both rows",
                $"{timeline.Model!.PredictedCount} predicted, {timeline.Model.ServerCount} server, {timeline.Model.Mismatches} mismatches");
            Check(step, "tick timing read", ticks.Ticks > 0, "TickTiming records", $"{ticks.Ticks} ticks, p99 {summary.P99Us} us");

            string? copy = Environment.GetEnvironmentVariable(ClientConfig.DataDirectoryVariable);
            if (string.IsNullOrWhiteSpace(copy))
            {
                step.log.Add("no MMORPG_DATA_DIR: the table reload is checked only in a Run-QaSmoke run (it never edits the repository's tables)");
            }
            else
            {
                int recovery = attack.RecoveryMs;
                int tuned = recovery + QaTimeouts.ReloadRecoveryDeltaMs;
                yield return ReloadRecovery(step, client, reload, copy!, attack.Id, recovery, tuned);
                Check(step, "reload applied", client.Catalog?.Find(attack.Id)?.Def.RecoveryMs == tuned,
                    $"{attack.Id} recoveryMs {tuned} on the client", (client.Catalog?.Find(attack.Id)?.Def.RecoveryMs ?? -1).ToString(CultureInfo.InvariantCulture));
                yield return ReloadRecovery(step, client, reload, copy!, attack.Id, tuned, recovery);
            }
            foreach (var id in new[] { Hotkeys.DevHitboxes, Hotkeys.DevTimeline, Hotkeys.DevTickTiming })
                yield return Press(_input!.ButtonForPath(Hotkeys.Find(_config, id)!.Binding));
        }

        /// <summary>Rewrites one action's <c>recoveryMs</c> in the run's tables copy and waits for both sides to apply it.</summary>
        private IEnumerator ReloadRecovery(QaStepResult step, PredictionDevClient client, DevTableReload reload, string directory,
            string actionId, int from, int to)
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetFullPath(directory), "ActionDef.json");
            string text = System.IO.File.ReadAllText(path);
            int row = text.IndexOf("\"id\": \"" + actionId + "\"", StringComparison.Ordinal);
            string field = "\"recoveryMs\": " + from.ToString(CultureInfo.InvariantCulture);
            int at = row < 0 ? -1 : text.IndexOf(field, row, StringComparison.Ordinal);
            if (at < 0)
                throw new QaFailure($"{actionId} has no {field} in {path}");
            int applied = reload.Applied;
            System.IO.File.WriteAllText(path, text.Substring(0, at) + "\"recoveryMs\": " + to.ToString(CultureInfo.InvariantCulture)
                + text.Substring(at + field.Length));
            step.log.Add($"{actionId} recoveryMs {from} -> {to} in {path}");
            yield return WaitUntil(() => reload.Applied > applied, QaTimeouts.ReloadSeconds,
                "the server to accept the reload and the client to apply it: " + reload.Status);
            step.log.Add($"server accepted at tick {reload.LastServerTick}; client: {reload.Status}");
            Check(step, "server accepted " + to, reload.LastServerResult == DevTelemetryLayout.ReloadAccepted, "accepted",
                reload.LastServerResult.ToString(CultureInfo.InvariantCulture));
            Check(step, "same tables " + to, reload.AppliedHash == reload.LastServerHash, reload.LastServerHash.ToString("x16"),
                reload.AppliedHash.ToString("x16"));
        }

        // ---- step plumbing

        /// <summary>
        /// fix/parry-feel: the parry against the boss (R-3.3-03, R-3.3-04). Walks to the Cyclops and, for each of its
        /// attacks the class can parry (not red; yellow and blue by armor class), presses the parry when the drawn attack is
        /// <see cref="QaTimeouts.ParryLeadTicks"/> before its hit (the hit resolves when Active starts, on the view clock).
        /// The first <c>ParrySuccess</c> must flash the P square and the boss; shot <c>-flash</c> while both show. Every
        /// parry result is logged (early, late, red, armor).
        /// </summary>
        private IEnumerator ParryBoss(QaStepResult step)
        {
            var dev = UnityEngine.Object.FindAnyObjectByType<PredictionDevClient>()
                      ?? throw new QaFailure("no PredictionDevClient in the world scene");
            var feed = dev.Feed ?? throw new QaFailure("no world feed");
            var boss = Rows<MonsterDef>().FirstOrDefault(m => m.Boss) ?? throw new QaFailure("no boss in MonsterDef");
            var point = Rows<SpawnPoints>().FirstOrDefault(p => p.MonsterId == boss.Id) ?? throw new QaFailure("the boss has no spawn point");
            _monsterMarker = QaMarkers.Load(ClientWorld.Directory("m1")).FirstOrDefault(m => m.id == point.MarkerId)
                             ?? throw new QaFailure("no marker " + point.MarkerId);
            _monsterId = boss.Id;
            _monsterNetId = (ushort)boss.NetId;
            var armor = ClassDef.Find(_config, _writer.Result.classId)?.ArmorClass ?? ArmorClass.Light;
            step.log.Add($"boss {boss.Id} at {_monsterMarker.id}; armor class {armor}; press {QaTimeouts.ParryLeadTicks} ticks before each hit");

            var walk = new ApproachResult();
            int firstHit = _tap.Hits.Count;
            // Smoke pf1: the straight path aggroed demon b, which killed the player at the boss. Go round the other spawns'
            // aggro along a lane north of the boss, then close in.
            int laneY = _monsterMarker.yCm + QaTimeouts.ParryBossLaneOffsetCm;
            yield return WalkTo(FixVec2.FromCm(_tap.OwnXCm, laneY), firstHit);
            yield return WalkTo(FixVec2.FromCm(_monsterMarker.xCm - QaTimeouts.ParryBossLaneOffsetCm, laneY), firstHit);
            yield return Approach(QaTimeouts.ParryBossApproachCm, QaTimeouts.WalkSeconds, walk);
            if (walk.Died)
                throw new QaFailure("the player died on the way to the boss: " + DescribeDeath(firstHit));
            uint bossId = _demonId;

            var parry = _input!.ButtonFor("Parry");
            int seenResults = _tap.Refusals.Count;
            uint lastStart = 0;
            int attempts = 0;
            bool success = false, pFlash = false, bossFlash = false;
            float started = Time.realtimeSinceStartup;
            while (!success && attempts < QaTimeouts.ParryBossAttempts)
            {
                if (OwnDead())
                    throw new QaFailure($"the player died at the boss after {attempts} parries: " + DescribeDeath(firstHit));
                if (Time.realtimeSinceStartup - started > QaTimeouts.ParryBossSeconds)
                    throw new QaFailure($"no parry success in {QaTimeouts.ParryBossSeconds:0} s ({attempts} parries)");
                yield return null;
                for (; seenResults < _tap.Refusals.Count; seenResults++)
                {
                    var result = _tap.Refusals[seenResults];
                    step.log.Add("parry result " + result.Reason);
                    success |= result.Reason == ActionReason.ParrySuccess;
                }
                if (!feed.Registry.Entities.TryGetValue(bossId, out var entity) || !feed.HasRenderTick
                    || entity.ActionState < Shared.Simulation.Actions.ActionState.Startup
                    || entity.ActionState > Shared.Simulation.Actions.ActionState.Active
                    || entity.ActionStartTick == lastStart || !feed.Catalog.TryGetIndex(entity.ActionNetId, out int index))
                    continue;
                if (!CanParry(entity.TelegraphColor, armor))
                {
                    lastStart = entity.ActionStartTick;
                    step.log.Add($"{feed.Catalog[index].Id} ({entity.TelegraphColor}): not parried, the class cannot parry it");
                    continue;
                }
                // The strike fires on its effect's tick; a projectile's hit depends on its flight, so only strikes are timed.
                var strike = feed.Catalog[index].Effects.FirstOrDefault(e => e.Def.Kind == EffectKind.Strike);
                if (strike.Def == null)
                    continue;
                uint hitTick = unchecked(entity.ActionStartTick + (uint)strike.FireTick);
                if (feed.RenderTick < hitTick - QaTimeouts.ParryLeadTicks)
                    continue;
                lastStart = entity.ActionStartTick;
                if (feed.RenderTick > hitTick - QaTimeouts.ParryLeadTicks + QaTimeouts.ParryLateSlackTicks)
                {
                    step.log.Add($"{feed.Catalog[index].Id}: seen at render tick {feed.RenderTick:0.00}, too close to its hit {hitTick}; skipped");
                    continue;
                }
                attempts++;
                step.log.Add($"{feed.Catalog[index].Id} ({entity.TelegraphColor}): parry at render tick {feed.RenderTick:0.00}, hit tick {hitTick}");
                yield return Press(parry);
            }
            Check(step, "a parry of a boss attack succeeds", success, "ParrySuccess", attempts + " parries, see the log");
            if (!success)
                yield break;
            // The result came with the hit: the P square and the boss flash for HudSettings.parryFlashMs / hitFlashMs. The
            // presenters animate in Update and the overlays place in LateUpdate, after this coroutine: watch a few frames.
            var settings = HudConfig.From(_config).Settings;
            float watchUntil = Time.realtimeSinceStartup + Math.Max(settings.ParryFlashMs, settings.HitFlashMs) / 1000f;
            bool shot = false;
            while (Time.realtimeSinceStartup < watchUntil)
            {
                yield return null;
                pFlash |= _hud?.ParryDodge?.ParryFlashing ?? false;
                bossFlash |= (_hud?.HitFlashes?.TintedRenderers ?? 0) > 0;
                if (!shot && (_hud?.HitFlashes?.TintedRenderers ?? 0) > 0)
                {
                    shot = true;
                    yield return Shot(step, "flash"); // the boss tinted; the P square flashes longer
                }
            }
            if (!shot && pFlash)
                yield return Shot(step, "flash");
            step.log.Add($"boss renderers that already had a property block (left alone): {_hud?.HitFlashes?.SkippedRenderers ?? 0}");
            Check(step, "P square flashes on the success", pFlash, "flashing (HudSettings.parryFlashMs)", pFlash ? "flashing" : "not flashing");
            Check(step, "the boss flashes on the success", bossFlash, "renderers tinted (hitFlashMs)", bossFlash ? "tinted" : "none");
        }

        /// <summary>Walks to a ground point along the grid path (parry-boss step); a death fails the step with its cause.</summary>
        private IEnumerator WalkTo(FixVec2 point, int firstHit)
        {
            var path = QaNavigation.FindPath(_grid, Own(), point)
                       ?? throw new QaFailure($"no walkable path from ({_tap.OwnXCm}, {_tap.OwnYCm}) to ({QaNavigation.Cm(point.X):0}, {QaNavigation.Cm(point.Y):0})");
            float started = Time.realtimeSinceStartup;
            while (DistanceCm(Own(), point) > QaTimeouts.WaypointReachedCm)
            {
                if (OwnDead())
                {
                    Walk(null);
                    throw new QaFailure("the player died on the way to the boss: " + DescribeDeath(firstHit));
                }
                if (Time.realtimeSinceStartup - started > QaTimeouts.WalkSeconds)
                    throw new QaFailure($"did not reach ({QaNavigation.Cm(point.X):0}, {QaNavigation.Cm(point.Y):0}) in {QaTimeouts.WalkSeconds:0} s");
                var waypoint = QaNavigation.NextWaypoint(_grid, Predicted(), path);
                Walk(waypoint);
                AimAt(waypoint);
                yield return null;
            }
            Walk(null);
        }

        /// <summary>R-3.3-04: red never, yellow with heavy armor, blue with medium or heavy, green and uncolored by anyone.</summary>
        private static bool CanParry(ParryColor color, ArmorClass armor) => color switch
        {
            ParryColor.Red => false,
            ParryColor.Yellow => armor == ArmorClass.Heavy,
            ParryColor.Blue => armor != ArmorClass.Light,
            _ => true,
        };

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

        /// <summary>Presses a kit action's button and notes when stamina was last spent (F2: the regeneration delay).</summary>
        private IEnumerator PressAction(QaKitAction action)
        {
            if (action.Def.StaminaCost > 0)
                _lastSpendAt = Time.realtimeSinceStartup;
            yield return Press(_input!.ButtonFor(action.Button));
        }

        private long SinceSpendMs() =>
            _lastSpendAt < 0f ? long.MaxValue : (long)((Time.realtimeSinceStartup - _lastSpendAt) * 1000f);

        /// <summary>How one press of a kit action went, from the client's predicted machine and the server's refusals.</summary>
        private sealed class CastResult
        {
            public bool Interrupted;
            public string Refused = "";
            public Shared.Simulation.Actions.ActionState Reached = Shared.Simulation.Actions.ActionState.Idle;

            /// <summary>R-3.1-10: the normal attack chain's later hits the predicted machine ran, in order.</summary>
            public readonly List<string> ChainHits = new List<string>();

            /// <summary>It reached Active (its effects fire) without a flinch, a stagger or a refusal.</summary>
            public bool Fired => !Interrupted && Refused.Length == 0 && Reached >= Shared.Simulation.Actions.ActionState.Active;

            public override string ToString() => Fired ? "fired"
                : Refused.Length > 0 ? "refused " + Refused
                : Interrupted ? "interrupted" : "reached " + Reached;
        }

        /// <summary>
        /// Presses <paramref name="action"/> and waits while the predicted machine runs it (a refused or interrupted action
        /// frees the player at once). F3: a fired action counts in <see cref="_fired"/>; the first channel the HUD cast bar
        /// names gets the <c>-cast</c> shot.
        /// </summary>
        private IEnumerator Cast(QaStepResult step, QaKitAction action, PredictionDevClient dev, CastResult result)
        {
            int refusals = _tap.Refusals.Count;
            yield return PressAction(action);
            float pressed = Time.realtimeSinceStartup;
            float barSince = -1f;
            // R-3.1-10: the normal attack is a chain; press again in each hit's chain window, up to its last hit.
            float seconds = action.ChainTotalMs / 1000f + QaTimeouts.PressSeconds * (1 + action.Chain.Count);
            int chainPressedFor = -1;
            string lastHit = action.Chain.Count > 0 ? action.Chain[action.Chain.Count - 1].Id : "";
            while (dev.Predictor != null && dev.Predictor.Actions.IsBusy && !OwnDead()
                   && Time.realtimeSinceStartup - pressed < seconds)
            {
                var machine = dev.Predictor.Actions;
                var state = machine.State;
                var running = machine.Action;
                if (action.Chain.Count > 0 && running != null)
                {
                    if (action.Chain.Any(hit => hit.Id == running.Id) && !result.ChainHits.Contains(running.Id))
                        result.ChainHits.Add(running.Id);
                    if (running.Id == lastHit && state == Shared.Simulation.Actions.ActionState.Active && _chainShot.Length == 0)
                    {
                        _chainShot = $"{lastHit} (shot {step.name}-chain)";
                        yield return Shot(step, "chain");
                        continue;
                    }
                    if (QaFightPlan.ChainPressDue(machine, chainPressedFor))
                    {
                        chainPressedFor = machine.ActionIndex;
                        yield return PressAction(action);
                        continue;
                    }
                }
                if (state == Shared.Simulation.Actions.ActionState.Flinched || state == Shared.Simulation.Actions.ActionState.Staggered)
                    result.Interrupted = true;
                else if (state > result.Reached && state <= Shared.Simulation.Actions.ActionState.Recovery)
                    result.Reached = state;
                if (_staggerStep != null && WatchStagger())
                    yield return Shot(_staggerStep, "stagger");
                if (state == Shared.Simulation.Actions.ActionState.Channel && _castBar.Length == 0 && CastBarNames(action))
                {
                    // Halfway through the channel, so the bar's fill and time left show.
                    if (barSince < 0f)
                        barSince = Time.realtimeSinceStartup;
                    if (Time.realtimeSinceStartup - barSince >= action.Def.ChannelMs / 2000f)
                    {
                        _castBar = $"{action.Id} ('{_login.Text.Get(UiStringCoverage.ActionId(action.Id))}', shot {step.name}-cast)";
                        yield return Shot(step, "cast");
                    }
                }
                yield return null;
            }
            for (int i = refusals; i < _tap.Refusals.Count; i++)
            {
                if (_tap.Refusals[i].ActionNetId == action.Def.NetId)
                    result.Refused = _tap.Refusals[i].Reason.ToString();
            }
            if (result.Fired)
                _fired[action.Id] = _fired.TryGetValue(action.Id, out int fired) ? fired + 1 : 1;
        }

        /// <summary>Spec 9.3 <c>cast_bar</c> is shown and names <paramref name="action"/>.</summary>
        private bool CastBarNames(QaKitAction action)
        {
            var bar = _hud != null ? _hud.GetComponent<UIDocument>()?.rootVisualElement?.Q(HudElementIds.CastBar) : null;
            if (bar == null || bar.style.display != DisplayStyle.Flex)
                return false;
            string name = _login.Text.Get(UiStringCoverage.ActionId(action.Id));
            return bar.Query<Label>().ToList().Any(label => label.text == name);
        }

        /// <summary>The snapshot's stamina and MP pay for it and the predicted machine is idle with it off cooldown.</summary>
        private bool UsableNow(QaKitAction action, PredictionDevClient dev)
        {
            var own = _tap.Registry.OwnResources;
            return own.Stamina >= action.ChainStaminaCost && own.Mp >= action.Def.MpCost && PredictedReady(dev, action);
        }

        /// <summary>
        /// R-3.3-01 stagger marker: notes whether the snapshots showed the demon staggered, whether its plate showed the
        /// marker, and whether the marker went away again while the demon lived. True the first time the marker shows (the
        /// <c>-stagger</c> shot).
        /// </summary>
        private bool WatchStagger()
        {
            if (_demonId == 0 || !_tap.Registry.Entities.TryGetValue(_demonId, out var demon))
                return false;
            if (demon.ActionState == Shared.Simulation.Actions.ActionState.Staggered)
                _sawDemonStaggered = true;
            bool shown = _hud?.NamePlates != null && _hud.NamePlates.StaggerMarkerShown(_demonId);
            if (shown)
                _sawStaggerMarker = true;
            else if (_sawStaggerMarker && demon.HpBucket > 0 && demon.ActionState != Shared.Simulation.Actions.ActionState.Dead)
                _staggerMarkerEnded = true;
            if (!shown || _staggerShot)
                return false;
            _staggerShot = true;
            return true;
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
            if (action.Slot == QaFightPlan.DodgeSlot)
                return loadout.DodgeIndex >= 0 && predictor.Actions.Charges(Shared.Data.Tables.ChargePool.Dodge) > 0;
            int index = action.Slot == 0 ? loadout.AttackIndex : loadout.SlotIndex(action.Slot);
            return index >= 0 && predictor.Actions.CooldownTicks(index) == 0;
        }

        private sealed class BackOffResult
        {
            public bool Found;
        }

        /// <summary>
        /// One leg of a back-off (QA wave 5 H2): <paramref name="distanceCm"/> away from the demon where it is now, along
        /// the most direct direction that stays out of no-entry regions and within <paramref name="maxFromHomeCm"/> of its
        /// <paramref name="home"/> (QaNavigation.TryBackOffTarget), for at most <see cref="QaTimeouts.BackOffSeconds"/>.
        /// </summary>
        private IEnumerator BackOff(bool[] noEntry, FixVec2 home, int maxFromHomeCm, int distanceCm,
            IReadOnlyList<(FixVec2 At, int RadiusCm)> avoid, BackOffResult result)
        {
            result.Found = false;
            var from = Own();
            var demon = _tap.Registry.Entities.TryGetValue(_demonId, out var seen) && QaWorldTap.TryGroundCm(seen, out int x, out int y)
                ? FixVec2.FromCm(x, y)
                : home;
            if (!QaNavigation.TryBackOffTarget(_grid, noEntry, from, demon, home, distanceCm, maxFromHomeCm, out var away, avoid))
                yield break;
            result.Found = true;
            float started = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - started < QaTimeouts.BackOffSeconds && !OwnDead()
                   && DistanceCm(Own(), away) > QaTimeouts.StuckCm)
            {
                Walk(away);
                yield return null;
            }
            Walk(null);
        }

        private sealed class StepBackResult
        {
            public bool InReach;
            public double DistanceCm;
        }

        /// <summary>
        /// Makes room for a skill whose forward dash would carry the player past a demon this close (QA wave 5 H3, Jump
        /// Strike): walks away along a back-off direction until the demon is in the upper half of the skill's reach window
        /// (it closes in during the startup), for at most <see cref="QaTimeouts.StepBackSeconds"/>, then faces it.
        /// </summary>
        private IEnumerator StepBack(QaKitAction action, bool[] noEntry, FixVec2 home, int maxFromHomeCm,
            IReadOnlyList<(FixVec2 At, int RadiusCm)> avoid, StepBackResult result)
        {
            int wantCm = Math.Min(action.ReachCm, action.PreferredCm + (action.ReachCm - action.PreferredCm) / 2);
            float started = Time.realtimeSinceStartup;
            result.InReach = false;
            while (!OwnDead() && Time.realtimeSinceStartup - started < QaTimeouts.StepBackSeconds)
            {
                if (!_tap.Registry.Entities.TryGetValue(_demonId, out var demon) || !QaWorldTap.TryGroundCm(demon, out int x, out int y))
                    break;
                var at = FixVec2.FromCm(x, y);
                result.DistanceCm = DistanceCm(Own(), at);
                if (result.DistanceCm >= wantCm && action.InReach(result.DistanceCm))
                {
                    Walk(null);
                    AimAt(at);
                    result.InReach = true;
                    yield break;
                }
                if (!QaNavigation.TryBackOffTarget(_grid, noEntry, Own(), at, home, (int)(wantCm - result.DistanceCm) + 100,
                        maxFromHomeCm, out var away, avoid))
                    break;
                Walk(away);
                yield return null;
            }
            Walk(null);
        }

        /// <summary>
        /// Presses each usable buff of <paramref name="buffs"/> and waits for the predicted machine to finish it. A cast
        /// that was not interrupted goes into <see cref="_castBeforeFight"/>: the kill step expects its status in the buff
        /// row and does not open with it again.
        /// </summary>
        private IEnumerator CastBuffs(List<QaKitAction> buffs, QaStepResult step)
        {
            var dev = UnityEngine.Object.FindAnyObjectByType<PredictionDevClient>();
            if (dev == null)
                yield break;
            foreach (var buff in buffs)
            {
                var own = _tap.Registry.OwnResources;
                if (own.Stamina < buff.Def.StaminaCost || own.Mp < buff.Def.MpCost || !PredictedReady(dev, buff))
                {
                    step.log.Add($"{buff.Id} not ready before the fight (ST {own.Stamina}, MP {own.Mp})");
                    continue;
                }
                var cast = new CastResult();
                yield return Cast(step, buff, dev, cast);
                if (cast.Interrupted)
                    continue;
                _castBeforeFight.Add(buff.Id);
                step.log.Add($"{buff.Id} cast before the fight, {DistanceCm(Own(), FixVec2.FromCm(_monsterMarker!.xCm, _monsterMarker.yCm)):0} cm from the demon's marker");
            }
        }

        /// <summary>
        /// QA wave 6 F3: presses each kit skill the fight never needs once, before the fight and outside the demon's aggro
        /// range. A move (<see cref="QaKitAction.Moves"/>: Dash, Back Step) goes along a clear direction away from the demon
        /// (the player faces so that the dash's <c>directionOffsetDeg</c> points there; pressed with no movement key held, the M1 dashes and the dodge go 180 degrees from it),
        /// and the server position must move at least <see cref="QaTimeouts.DashPercentOfDistance"/> of its distance that
        /// way. A utility (<see cref="QaKitAction.Utility"/>: Simple Cleanse) must fire and its MP must leave the snapshot.
        /// Then it waits for the stamina to fill (the regeneration of the tables) before the fight.
        /// </summary>
        private IEnumerator KitTour(List<QaKitAction> tour, QaStepResult step)
        {
            var dev = UnityEngine.Object.FindAnyObjectByType<PredictionDevClient>();
            if (tour.Count == 0 || dev == null)
                yield break;
            var regen = QaStaminaRegen.From(_config);
            var monster = MonsterDef.Find(_config, _monsterId)!;
            var home = FixVec2.FromCm(_monsterMarker!.xCm, _monsterMarker.yCm);
            var noEntry = QaRegions.NoEntry(ClientWorld.Directory("m1"), _config);
            var avoid = OtherSpawnAggro();
            avoid.Add((home, monster.AggroRangeCm + QaTimeouts.AvoidAggroMarginCm));
            foreach (var action in tour)
            {
                var meters = _tap.Registry.OwnResources;
                int waitMs = Math.Max(regen.MsUntil(meters.Stamina, action.Def.StaminaCost, SinceSpendMs()), action.Def.CooldownMs);
                float waitFrom = Time.realtimeSinceStartup;
                while (Alive("waiting to press " + action.Id) && !UsableNow(action, dev)
                       && Time.realtimeSinceStartup - waitFrom < waitMs / 1000f + QaTimeouts.UiSeconds)
                    yield return null;
                var from = Own();
                var cast = new CastResult();
                if (action.Moves)
                {
                    if (!QaNavigation.TryBackOffTarget(_grid, noEntry, from, home, home, action.DashCm + QaTimeouts.DashClearanceCm,
                            int.MaxValue, out var clear, avoid))
                        throw new QaFailure($"no clear {action.DashCm + QaTimeouts.DashClearanceCm} cm for {action.Id} from ({_tap.OwnXCm}, {_tap.OwnYCm})");
                    double dx = QaNavigation.Cm(clear.X) - QaNavigation.Cm(from.X), dy = QaNavigation.Cm(clear.Y) - QaNavigation.Cm(from.Y);
                    double length = Math.Max(1.0, Math.Sqrt(dx * dx + dy * dy));
                    dx /= length;
                    dy /= length;
                    // The move goes at directionOffsetDeg from the facing: face that much the other way (180: toward the demon).
                    double turn = -action.DashOffsetDeg * Math.PI / 180.0;
                    double fx = dx * Math.Cos(turn) + dy * Math.Sin(turn), fy = -dx * Math.Sin(turn) + dy * Math.Cos(turn);
                    AimAt(FixVec2.FromCm((int)Math.Round(QaNavigation.Cm(from.X) + fx * QaTimeouts.AimAheadCm),
                        (int)Math.Round(QaNavigation.Cm(from.Y) + fy * QaTimeouts.AimAheadCm)));
                    yield return Wait(3 * (float)_tap.TickMs / 1000f); // the facing reaches the server with the next inputs
                    from = Own();
                    yield return Cast(step, action, dev, cast);
                    yield return Wait(3 * (float)_tap.TickMs / 1000f); // the server position of the move's end
                    double moved = (QaNavigation.Cm(Own().X) - QaNavigation.Cm(from.X)) * dx + (QaNavigation.Cm(Own().Y) - QaNavigation.Cm(from.Y)) * dy;
                    int wantCm = action.DashCm * QaTimeouts.DashPercentOfDistance / 100;
                    Check(step, action.Id + " moves the player", cast.Fired && moved >= wantCm,
                        $"fired, >= {wantCm} cm along its direction ({action.DashCm} cm at {action.DashOffsetDeg} degrees from the facing)",
                        $"{cast}, {moved:0} cm from ({QaNavigation.Cm(from.X):0}, {QaNavigation.Cm(from.Y):0}) to ({_tap.OwnXCm}, {_tap.OwnYCm})");
                }
                else
                {
                    int mpBefore = _tap.Registry.OwnResources.Mp;
                    yield return Cast(step, action, dev, cast);
                    yield return Wait(3 * (float)_tap.TickMs / 1000f);
                    int mpAfter = _tap.Registry.OwnResources.Mp;
                    Check(step, action.Id + " fires", cast.Fired && mpAfter < mpBefore, $"fired, its {action.Def.MpCost} MP spent",
                        $"{cast}, MP {mpBefore} -> {mpAfter}");
                    // Nothing in the M1 fight puts a status or a build-up on the player (the demon's attacks carry none).
                    step.log.Add($"{action.Id}: nothing to clear before the fight (the M1 demon applies no status or build-up); only that it fires is checked");
                }
            }
            // The fight starts with full stamina again (StartingValuesM1.staminaRegenDelayMs and staminaRegenPerSec).
            var own = _tap.Registry.OwnResources;
            int refillMs = regen.MsUntil(own.Stamina, own.MaxStamina, SinceSpendMs());
            float refillFrom = Time.realtimeSinceStartup;
            while (Alive("waiting for stamina") && _tap.Registry.OwnResources.Stamina < own.MaxStamina
                   && Time.realtimeSinceStartup - refillFrom < refillMs / 1000f + QaTimeouts.UiSeconds)
                yield return null;
            step.log.Add($"kit tour done: ST {_tap.Registry.OwnResources.Stamina}/{own.MaxStamina}, MP {_tap.Registry.OwnResources.Mp}/{own.MaxMp}");
        }

        /// <summary>
        /// The aggro circles of the other spawn points (SpawnPoints and the world's markers): each marker with its monster's
        /// <c>aggroRangeCm</c> plus <see cref="QaTimeouts.AvoidAggroMarginCm"/>. Back-offs and step-backs end outside them.
        /// </summary>
        private List<(FixVec2 At, int RadiusCm)> OtherSpawnAggro()
        {
            var markers = QaMarkers.Load(ClientWorld.Directory("m1"));
            var circles = new List<(FixVec2 At, int RadiusCm)>();
            foreach (var point in Rows<SpawnPoints>())
            {
                var marker = markers.FirstOrDefault(m => m.id == point.MarkerId);
                var def = MonsterDef.Find(_config, point.MonsterId);
                if (marker == null || def == null || marker.id == _monsterMarker!.id)
                    continue;
                circles.Add((FixVec2.FromCm(marker.xCm, marker.yCm), def.AggroRangeCm + QaTimeouts.AvoidAggroMarginCm));
            }
            return circles;
        }

        /// <summary>True while the player lives; a death after the kill fails the step with its cause, not a timeout.</summary>
        private bool Alive(string during)
        {
            if (OwnDead())
                throw new QaFailure("the player died while " + during + ": " + DescribeDeath(0));
            return true;
        }

        /// <summary>Marks the expected statuses whose chip the buff row shows; true when any is shown now.</summary>
        private bool WatchBuffRow(Dictionary<string, bool> seen)
        {
            if (seen.Count == 0 || _hud?.BuffRow == null)
                return false;
            var chips = _hud.BuffRow.ChipTexts();
            bool any = false;
            foreach (var status in seen.Keys.ToList())
            {
                if (Array.IndexOf(chips, _login.Text.Get(UiStringCoverage.StatusId(status))) < 0)
                    continue;
                seen[status] = true;
                any = true;
            }
            return any;
        }

        /// <summary>A kit action as the step log lists it.</summary>
        private static string Describe(QaKitAction a)
        {
            var parts = new List<string> { a.Button };
            if (a.Damages)
                parts.Add($"{a.DamagePercent}%, reach {(a.MinReachCm > 0 ? a.MinReachCm + "-" : "")}{a.ReachCm} cm");
            if (a.HealsSelf)
                parts.Add("heal");
            if (a.SelfStatusId.Length > 0)
                parts.Add((a.OpensFight ? "opening buff " : "status ") + a.SelfStatusId);
            if (a.Moves)
                parts.Add($"move {a.DashCm} cm at {a.DashOffsetDeg} degrees, pressed in walk-to-demon");
            else if (a.Utility)
                parts.Add("utility, pressed in walk-to-demon");
            if (a.Def.ChannelMs > 0)
                parts.Add($"channel {a.Def.ChannelMs} ms");
            return $"{a.Id} ({string.Join(", ", parts)})";
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
            lowestHp = Math.Min(lowestHp, own.Hp); // H6: hits that landed after the loop's last look count too
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
            lowestHp = Math.Min(lowestHp, own.Hp);
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
