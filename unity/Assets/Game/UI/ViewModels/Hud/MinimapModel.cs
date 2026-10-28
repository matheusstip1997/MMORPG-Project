#nullable enable
using System;
using System.Collections.Generic;
using Game.Gameplay.Entities;
using Game.Gameplay.Streaming;
using Game.UI.Config;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;
using Shared.World;
using UnityEngine;

namespace Game.UI.ViewModels.Hud
{
    /// <summary>One marker on the minimap: its kind and its world position (ground cm).</summary>
    public readonly struct MinimapPoint
    {
        public MinimapPoint(MinimapMarkerStyle style, int xCm, int yCm)
        {
            Style = style;
            XCm = xCm;
            YCm = yCm;
        }

        public MinimapMarkerStyle Style { get; }
        public int XCm { get; }
        public int YCm { get; }
    }

    /// <summary>
    /// The minimap v0 (spec 9.3 "Minimap v0 marker set", 9.12; WP-M2-10b): the map image and the markers of every
    /// <see cref="MinimapMarkerDef"/> source, from the exported world and the world client's feed. Static markers (NPCs,
    /// passes and the gate) are found once when the world is set; the run's camps change with the run's revision, the death
    /// spot with the own HP; the player's position and facing are read every frame. <see cref="Changed"/> fires when the
    /// marker list changed (<see cref="Revision"/>), not for the player's movement. Display only: the HUD decides nothing.
    /// </summary>
    public sealed class MinimapModel : ViewModel
    {
        private readonly MinimapConfig _config;
        private readonly List<MinimapPoint> _static = new List<MinimapPoint>();
        private readonly List<MinimapPoint> _points = new List<MinimapPoint>();
        private readonly List<Vector2Int> _party = new List<Vector2Int>();
        private readonly Dictionary<string, List<Vector2Int>> _camps = new Dictionary<string, List<Vector2Int>>(StringComparer.Ordinal);
        private WorldIndex? _index;
        private RunStatus? _run;
        private bool _alive;
        private bool _hasDeath;
        private Vector2Int _death;
        private double _deathAtMs;
        private bool _worldTried;

        public MinimapModel(MinimapConfig config)
        {
            _config = config;
        }

        public MinimapConfig Config => _config;

        /// <summary>The map image; null until the world is set.</summary>
        public MinimapTexture? Texture { get; private set; }

        public bool HasWorld => Texture != null;

        /// <summary>Why the world could not be read (a developer message, the map stays empty); empty when fine.</summary>
        public string WorldError { get; private set; } = string.Empty;

        public bool HasPlayer { get; private set; }
        public int PlayerXCm { get; private set; }
        public int PlayerYCm { get; private set; }

        /// <summary>16-bit binary angle (0 = north, +Y; clockwise).</summary>
        public ushort Facing { get; private set; }

        /// <summary>The arrow's rotation in degrees, clockwise from north (the UI's rotation sense).</summary>
        public float ArrowDegrees => Facing * (360f / 65536f);

        /// <summary>Own level; 0 before the first <c>ProgressionState</c>.</summary>
        public int Level { get; private set; }

        /// <summary>Every marker but the player's, in draw order (party members included); changes with <see cref="Revision"/>.</summary>
        public IReadOnlyList<MinimapPoint> Points => _points;

        /// <summary>Counts the changes of <see cref="Points"/>.</summary>
        public int Revision { get; private set; }

        /// <summary>Party members' positions (ground cm). Parties are M4: nothing fills it in M2.</summary>
        public IReadOnlyList<Vector2Int> PartyMembers => _party;

        public bool IsShown(MinimapMarkerStyle style) => Level >= style.VisibleFromLevel;

        /// <summary>
        /// A world position in the frame (pixels from its top-left corner, y down), north-up and centered on the player, at
        /// <see cref="MinimapSettings.CmPerPx"/>.
        /// </summary>
        public Vector2 ToFrame(int xCm, int yCm, float frameWidth, float frameHeight)
        {
            float scale = _config.Settings.CmPerPx;
            return new Vector2(frameWidth * 0.5f + (xCm - PlayerXCm) / scale, frameHeight * 0.5f - (yCm - PlayerYCm) / scale);
        }

        /// <summary>Reads what the minimap shows from the feed: the world once, then the player, level, run and HP. Allocates nothing per frame.</summary>
        public void Update(WorldClientFeed feed, double nowMs)
        {
            if (!_worldTried && feed.WorldId.Length > 0)
            {
                _worldTried = true;
                if (ClientWorld.TryLoadIndex(feed.WorldId, out var index, out var error))
                    SetWorld(feed.Predictor.Grid, index!);
                else
                {
                    WorldError = error;
                    Debug.LogError($"[HUD] Minimap: {error}");
                }
            }
            if (feed.Predictor.HasState)
            {
                var state = feed.Predictor.State;
                SetPlayer(FixedPoint.ToCmFloor(state.Position.X), FixedPoint.ToCmFloor(state.Position.Y), state.Facing);
            }
            SetLevel(feed.Progression?.Level ?? 0);
            SetRun(feed.Run);
            if (feed.Registry.HasOwnResources)
                SetOwnHp(feed.Registry.OwnResources.Hp, nowMs);
            Expire(nowMs);
        }

        /// <summary>The exported world: builds the map image and the static markers (NPCs, passes, the gate) and the camps.</summary>
        public void SetWorld(WorldGrid grid, WorldIndex index)
        {
            _index = index;
            Texture = MinimapTexture.From(grid, index, _config);
            _static.Clear();
            foreach (var style in _config.Markers)
            {
                if (style.Source == MinimapSource.Npc)
                {
                    foreach (var marker in index.Markers)
                    {
                        if (marker.Kind == MarkerKind.Npc && _config.TryGetNpcKind(marker.DefId, out var kind)
                            && Array.IndexOf(style.Row.NpcKinds, kind) >= 0)
                            _static.Add(new MinimapPoint(style, marker.XCm, marker.YCm));
                    }
                }
                else if (style.Source == MinimapSource.RegionPass && style.Row.Regions.Length == 2)
                {
                    foreach (var crossing in Crossings(grid, index.RegionIndexOf(style.Row.Regions[0]), index.RegionIndexOf(style.Row.Regions[1])))
                        _static.Add(new MinimapPoint(style, crossing.x, crossing.y));
                }
            }
            _camps.Clear();
            Rebuild();
        }

        public void SetPlayer(int xCm, int yCm, ushort facing)
        {
            HasPlayer = true;
            PlayerXCm = xCm;
            PlayerYCm = yCm;
            Facing = facing;
        }

        public void SetLevel(int level)
        {
            if (level == Level)
                return;
            Level = level;
            Rebuild();
        }

        /// <summary>The run (<c>RunStatus</c>, protocol 22); the camp hints change only with a new message.</summary>
        public void SetRun(RunStatus? run)
        {
            if (ReferenceEquals(run, _run))
                return;
            _run = run;
            Rebuild();
        }

        /// <summary>The own HP from the snapshot: a turn from alive to 0 is a death, kept at the player's position (spec 9.5).</summary>
        public void SetOwnHp(int hp, double nowMs)
        {
            if (hp > 0)
            {
                _alive = true;
                return;
            }
            if (!_alive || !HasPlayer)
                return;
            _alive = false;
            _hasDeath = true;
            _death = new Vector2Int(PlayerXCm, PlayerYCm);
            _deathAtMs = nowMs;
            Rebuild();
        }

        /// <summary>Drops the death spot once <see cref="MinimapSettings.DeathSpotMs"/> passed.</summary>
        public void Expire(double nowMs)
        {
            if (_hasDeath && nowMs - _deathAtMs >= _config.Settings.DeathSpotMs)
            {
                _hasDeath = false;
                Rebuild();
            }
        }

        /// <summary>Party members' positions (M4 parties call it; nothing does in M2).</summary>
        public void SetParty(IReadOnlyList<Vector2Int> members)
        {
            _party.Clear();
            for (int i = 0; i < members.Count; i++)
                _party.Add(members[i]);
            Rebuild();
        }

        /// <summary>The camps of a monster type: its spawn markers, each joined to the first camp within the merge radius.</summary>
        public IReadOnlyList<Vector2Int> CampsOf(string monsterId)
        {
            if (_camps.TryGetValue(monsterId, out var camps))
                return camps;
            camps = new List<Vector2Int>();
            _camps[monsterId] = camps;
            if (_index == null)
                return camps;
            long radiusSq = (long)_config.Settings.CampMergeRadiusCm * _config.Settings.CampMergeRadiusCm;
            var firsts = new List<Vector2Int>();
            var sums = new List<(long X, long Y, int Count)>();
            foreach (var id in _config.SpawnMarkersOf(monsterId))
            {
                if (!_index.TryGetMarker(id, out var marker))
                    continue;
                int camp = -1;
                for (int i = 0; i < firsts.Count && camp < 0; i++)
                {
                    long dx = marker.XCm - firsts[i].x, dy = marker.YCm - firsts[i].y;
                    if (dx * dx + dy * dy <= radiusSq)
                        camp = i;
                }
                if (camp < 0)
                {
                    firsts.Add(new Vector2Int(marker.XCm, marker.YCm));
                    sums.Add((marker.XCm, marker.YCm, 1));
                }
                else
                    sums[camp] = (sums[camp].X + marker.XCm, sums[camp].Y + marker.YCm, sums[camp].Count + 1);
            }
            foreach (var sum in sums)
                camps.Add(new Vector2Int((int)(sum.X / sum.Count), (int)(sum.Y / sum.Count)));
            return camps;
        }

        /// <summary>
        /// Where the walkable cells of region <paramref name="a"/> touch walkable cells of region <paramref name="b"/> (a 4-neighbour):
        /// one point per connected crossing (8-neighbour clusters), at its cells' mean, in ground cm. On the m1 world these are the
        /// ridge passes between the rings (spec 12.8); a ridge has no walkable cell, so it never counts.
        /// </summary>
        public static List<Vector2Int> Crossings(WorldGrid grid, int a, int b)
        {
            var result = new List<Vector2Int>();
            if (a <= 0 || b <= 0 || a == b)
                return result;
            int width = grid.Width, height = grid.Height;
            var marked = new bool[width * height];
            for (int row = 0; row < height; row++)
            {
                for (int column = 0; column < width; column++)
                {
                    int region = Walkable(grid, column, row);
                    if (region != a && region != b)
                        continue;
                    int other = region == a ? b : a;
                    if (Walkable(grid, column + 1, row) == other)
                        marked[row * width + column] = marked[row * width + column + 1] = true;
                    if (Walkable(grid, column, row + 1) == other)
                        marked[row * width + column] = marked[(row + 1) * width + column] = true;
                }
            }
            var stack = new Stack<int>();
            for (int start = 0; start < marked.Length; start++)
            {
                if (!marked[start])
                    continue;
                long sumX = 0, sumY = 0;
                int count = 0;
                marked[start] = false;
                stack.Push(start);
                while (stack.Count > 0)
                {
                    int cell = stack.Pop();
                    int column = cell % width, row = cell / width;
                    sumX += column;
                    sumY += row;
                    count++;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int c = column + dx, r = row + dy;
                            if (grid.InBounds(c, r) && marked[r * width + c])
                            {
                                marked[r * width + c] = false;
                                stack.Push(r * width + c);
                            }
                        }
                    }
                }
                // The cluster's mean cell, at the cell center.
                int cellCm = grid.CellSizeCm;
                result.Add(new Vector2Int(grid.OriginXCm + (int)(sumX * cellCm / count) + cellCm / 2,
                    grid.OriginYCm + (int)(sumY * cellCm / count) + cellCm / 2));
            }
            return result;
        }

        /// <summary>The region index of a walkable cell; -1 for a blocked or outside cell.</summary>
        private static int Walkable(WorldGrid grid, int column, int row) =>
            grid.InBounds(column, row) && grid.IsWalkableCell(column, row) ? grid.RegionIndexOfCell(column, row) : -1;

        private void Rebuild()
        {
            _points.Clear();
            foreach (var style in _config.Markers)
            {
                if (!IsShown(style))
                    continue;
                switch (style.Source)
                {
                    case MinimapSource.Npc:
                    case MinimapSource.RegionPass:
                        foreach (var point in _static)
                        {
                            if (ReferenceEquals(point.Style, style))
                                _points.Add(point);
                        }
                        break;
                    case MinimapSource.RunCamp:
                        AddCamps(style);
                        break;
                    case MinimapSource.DeathSpot:
                        if (_hasDeath)
                            _points.Add(new MinimapPoint(style, _death.x, _death.y));
                        break;
                    case MinimapSource.PartyMember:
                        foreach (var member in _party)
                            _points.Add(new MinimapPoint(style, member.x, member.y));
                        break;
                }
            }
            Revision++;
            NotifyChanged();
        }

        /// <summary>A hint on each camp of the active run's active steps of the row's kinds (spec 9.3; every camp of the type).</summary>
        private void AddCamps(MinimapMarkerStyle style)
        {
            if (_run == null || _run.State != RunState.Active)
                return;
            foreach (var step in _run.Steps)
            {
                if (step.State != RunStepState.Active || Array.IndexOf(style.Row.QuestKinds, step.Kind) < 0
                    || !_config.TryGetMonsterId(step.MonsterNetId, out var monsterId))
                    continue;
                foreach (var camp in CampsOf(monsterId))
                    _points.Add(new MinimapPoint(style, camp.x, camp.y));
            }
        }
    }
}
