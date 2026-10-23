#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Game.Gameplay.Entities;
using Game.Gameplay.Streaming;
using Game.UI.Config;
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
    /// none), enter world, walk to the demon, kill it with normal attacks, shoot Arrow Shot at a wall, wait for the
    /// respawn. It drives the game only through <see cref="QaInput"/> (virtual keyboard and mouse) and
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
            "title", "dev-login", "server-select", "character", "enter-world", "walk-to-demon", "kill-demon", "arrow-wall",
            "wait-respawn",
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

        private QaScenario(string runDir, ConfigSet config, WorldGrid grid)
        {
            _config = config;
            _login = LoginConfig.From(config);
            _grid = grid;
            _writer = new QaRunWriter(runDir, StepNames, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            _writer.Result.devLogin = CommandLineValue("-devLogin");
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
                    Title, DevLogin, ServerSelect, Character, EnterWorld, WalkToDemon, KillDemon, ArrowAtWall, WaitRespawn,
                };
                for (int i = 0; i < bodies.Length; i++)
                {
                    var step = _writer.Result.steps[i];
                    yield return RunStep(step, bodies[i]);
                    if (!step.Passed)
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
                QaLoginDriver.Click(classes[0]);
                yield return WaitUntil(() => QaLoginDriver.CanClick(_ui.Find<Button>(LoginScreens.CharacterCreate, CreateConfirm)),
                    QaTimeouts.UiSeconds, "Create to be enabled (name '" + name + "')");
                step.log.Add($"creating '{name}' ({_login.Creation.AllowedInitialClasses[0]})");
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
            // Visuals load asynchronously; a view with no renderer at all after that is a broken prefab or import.
            float waitVisual = Time.realtimeSinceStartup;
            while (CountRenderers(_views!.LocalView!) == 0 && Time.realtimeSinceStartup - waitVisual < QaTimeouts.VisualSeconds)
                yield return null;
            int drawn = CountRenderers(_views.LocalView!);
            Check(step, "local player drawn", drawn > 0, "renderers on the local view (if 0: reimport the visuals, unity/Assets/Game/Dev/README.md)",
                DescribeView(_views.LocalView!, _rig!.Camera));
            yield return Settle();
            yield return Shot(step);
        }

        private IEnumerator WalkToDemon(QaStepResult step)
        {
            PickMonster();
            var marker = _monsterMarker!;
            int approachCm = BasicAttackReachCm() * QaTimeouts.ApproachPercentOfReach / 100;
            step.log.Add($"target {_monsterId} at marker {marker.id} ({marker.xCm}, {marker.yCm}); stop within {approachCm} cm");

            var target = FixVec2.FromCm(marker.xCm, marker.yCm);
            var path = QaNavigation.FindPath(_grid, Own(), target)
                       ?? throw new QaFailure($"no walkable path from ({_tap.OwnXCm}, {_tap.OwnYCm}) to marker {marker.id}");
            float started = Time.realtimeSinceStartup;
            float progressAt = started;
            var progressFrom = Own();
            double distance = double.MaxValue;
            while (true)
            {
                var demon = NearestDemon(marker);
                if (demon != null && QaWorldTap.TryGroundCm(demon, out int dx, out int dy))
                {
                    _demonId = demon.Id;
                    var seen = FixVec2.FromCm(dx, dy);
                    if (QaNavigation.Distance(QaNavigation.Cm(seen.X), QaNavigation.Cm(seen.Y), QaNavigation.Cm(target.X), QaNavigation.Cm(target.Y)) > 100)
                    {
                        target = seen;
                        path = QaNavigation.FindPath(_grid, Own(), target) ?? path;
                    }
                }
                distance = DistanceCm(Own(), target);
                if (distance <= approachCm && _demonId != 0)
                    break;
                if (Time.realtimeSinceStartup - started > QaTimeouts.WalkSeconds)
                    throw new QaFailure($"did not reach the demon in {QaTimeouts.WalkSeconds} s (still {distance:0} cm away, demon seen: {_demonId != 0})");
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
            Check(step, "demon in sight", _demonId != 0, _monsterId + " entity", "entity " + _demonId);
            Check(step, "reached the demon", distance <= approachCm, "<= " + approachCm + " cm", distance.ToString("0", CultureInfo.InvariantCulture) + " cm");
            step.log.Add($"walked in {Time.realtimeSinceStartup - started:0.0} s to ({_tap.OwnXCm}, {_tap.OwnYCm})");
            yield return Settle();
            yield return Shot(step);
        }

        private IEnumerator KillDemon(QaStepResult step)
        {
            var bindings = _config.Single<ActionBindings>();
            var attack = ActionDef.Find(_config, bindings.NormalAttack) ?? throw new QaFailure("no ActionDef " + bindings.NormalAttack);
            var monster = MonsterDef.Find(_config, _monsterId)!;
            int reachCm = BasicAttackReachCm() * QaTimeouts.ApproachPercentOfReach / 100;
            var attackButton = _input!.ButtonFor("Attack");
            int firstHit = _tap.Hits.Count;
            byte firstBucket = _tap.Registry.Entities.TryGetValue(_demonId, out var demon) ? demon.HpBucket : (byte)0;
            byte lowestBucket = firstBucket;
            int presses = 0;
            bool fightShot = false;
            float started = Time.realtimeSinceStartup;
            while (_tap.Registry.Entities.TryGetValue(_demonId, out demon))
            {
                if (Time.realtimeSinceStartup - started > QaTimeouts.KillSeconds)
                    throw new QaFailure($"the demon still lives after {QaTimeouts.KillSeconds} s ({presses} attacks, HP bucket {demon.HpBucket})");
                if (_tap.Disconnected)
                    throw new QaFailure("disconnected during the fight");
                lowestBucket = Math.Min(lowestBucket, demon.HpBucket);
                if (!QaWorldTap.TryGroundCm(demon, out int x, out int y))
                {
                    yield return null;
                    continue;
                }
                var at = FixVec2.FromCm(x, y);
                AimAt(at);
                if (DistanceCm(Own(), at) > reachCm)
                {
                    Walk(at); // the demon moved (monster AI): follow it
                    yield return null;
                    continue;
                }
                Walk(null);
                if (_tap.Registry.OwnResources.Stamina < attack.StaminaCost)
                {
                    yield return null;
                    continue;
                }
                yield return Press(attackButton);
                presses++;
                yield return Wait(attack.TotalMs() / 1000f);
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
            step.log.Add($"{presses} attacks, {hits.Count} hits, {damage} damage, death at tick {_deathTick}");
            Check(step, "hit messages on the demon", hits.Count > 0, "> 0 hits from us", hits.Count.ToString(CultureInfo.InvariantCulture));
            Check(step, "HP bucket went down", lowestBucket < firstBucket, "below " + firstBucket, lowestBucket.ToString(CultureInfo.InvariantCulture));
            Check(step, "killing hit", hits.Any(hit => hit.Entry.Kind == HitKind.Killed), "a Killed hit", string.Join(",", hits.Select(hit => hit.Entry.Kind).Distinct()));
            Check(step, "damage covers max HP", damage >= monster.MaxHp, ">= " + monster.MaxHp + " (MonsterDef)", damage.ToString(CultureInfo.InvariantCulture));
            yield return Settle();
            yield return Shot(step);
        }

        private IEnumerator ArrowAtWall(QaStepResult step)
        {
            var bindings = _config.Single<ActionBindings>();
            int slot = Array.IndexOf(bindings.SkillSlots, "skill.arrowShot") + 1;
            if (slot <= 0)
                throw new QaFailure("skill.arrowShot is on no hotbar slot (ActionBindings)");
            var skill = ActionDef.Find(_config, "skill.arrowShot")!;
            var effect = Rows<ActionEffectDef>().First(row => row.ActionId == skill.Id && row.Kind == EffectKind.Projectile);
            var arrow = ProjectileDef.Find(_config, effect.ProjectileId)!;
            var avoid = _tap.Registry.Entities.Values.Where(e => e.Kind == EntityKind.Monster)
                .Select(e => QaWorldTap.TryGroundCm(e, out int x, out int y) ? FixVec2.FromCm(x, y) : Own()).ToList();
            if (!QaNavigation.TryFindWall(_grid, Own(), QaTimeouts.ShotClearanceCm, arrow.RangeCm * QaTimeouts.WallPercentOfRange / 100,
                    QaTimeouts.WallRays, avoid, QaTimeouts.ShotClearanceCm, out var wall, out int wallCm))
                throw new QaFailure($"no wall within {arrow.RangeCm * QaTimeouts.WallPercentOfRange / 100} cm of ({_tap.OwnXCm}, {_tap.OwnYCm})");
            step.log.Add($"aiming at the wall at ({QaNavigation.Cm(wall.X):0}, {QaNavigation.Cm(wall.Y):0}), {wallCm} cm away; hotbar slot {slot}");
            AimAt(wall);
            yield return Wait(3 * (float)_tap.TickMs / 1000f);
            float waitStamina = Time.realtimeSinceStartup;
            while (_tap.Registry.OwnResources.Stamina < skill.StaminaCost && Time.realtimeSinceStartup - waitStamina < QaTimeouts.ProjectileSeconds)
                yield return null;

            int before = _tap.Projectiles.Count;
            yield return Press(_input!.ButtonFor("Hotbar" + slot.ToString(CultureInfo.InvariantCulture)));
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
            if (failure == null && step.checks.All(c => c.pass))
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

        /// <summary>How far the normal attack's strike reaches from the attacker's center (HitShapeDef of its strike effects).</summary>
        private int BasicAttackReachCm()
        {
            string attack = _config.Single<ActionBindings>().NormalAttack;
            var effects = Rows<ActionEffectDef>().Where(e => e.ActionId == attack && e.Kind == EffectKind.Strike).Select(e => e.Id).ToList();
            int reach = Rows<HitShapeDef>().Where(s => effects.Contains(s.EffectId))
                .Select(s => s.ForwardOffsetCm + Math.Max(s.RangeCm, s.LengthCm + s.RadiusCm)).DefaultIfEmpty(0).Max();
            if (reach <= 0)
                throw new QaFailure($"the normal attack {attack} has no strike shape (HitShapeDef)");
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
