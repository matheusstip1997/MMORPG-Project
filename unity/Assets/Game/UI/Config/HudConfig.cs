#nullable enable
using System;
using System.Collections.Generic;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Simulation.Actions;
using UnityEngine;

namespace Game.UI.Config
{
    /// <summary>
    /// The config tables the HUD reads (spec 9.7, backlog I), from a loaded and validated <see cref="ConfigSet"/>: layout,
    /// settings, theme, text, and the lookups by net id the widgets need (class, monster, status, action shapes).
    /// </summary>
    public sealed class HudConfig
    {
        private readonly Dictionary<string, HudLayout> _layout = new Dictionary<string, HudLayout>(StringComparer.Ordinal);
        private readonly Dictionary<int, MonsterDef> _monsters = new Dictionary<int, MonsterDef>();
        private readonly Dictionary<int, StatusInfo> _statuses = new Dictionary<int, StatusInfo>();
        private readonly Dictionary<int, TelegraphShape[]> _shapesByActionNetId = new Dictionary<int, TelegraphShape[]>();

        public HudConfig(HudSettings settings, HudThemeTokens tokens, IEnumerable<HudLayout> layout, UiText text, ReasonText reasons,
            ActionCatalog actions, ChatSettings chat)
        {
            Settings = settings;
            Theme = new HudTheme(tokens);
            Text = text;
            Reasons = reasons;
            Actions = actions;
            Chat = chat;
            foreach (var row in layout)
                _layout[row.Id] = row;
        }

        public HudSettings Settings { get; }

        public HudTheme Theme { get; }

        public UiText Text { get; }

        public ReasonText Reasons { get; }

        public ActionCatalog Actions { get; private set; }

        /// <summary>Local chat limits (R-6.5-01): the input length and how many lines the panel keeps.</summary>
        public ChatSettings Chat { get; }

        public IEnumerable<HudLayout> LayoutRows => _layout.Values;

        public bool TryGetLayout(string elementId, out HudLayout row) => _layout.TryGetValue(elementId, out row!);

        /// <summary>The <c>MonsterDef</c> a monster spawn names (spec 1.7: its def id is the net id), or null.</summary>
        public MonsterDef? Monster(ushort netId) => _monsters.TryGetValue(netId, out var def) ? def : null;

        /// <summary>A status of the own snapshot by <c>StatusEffectDef.netId</c>, with its buff row icon row, or null.</summary>
        public StatusInfo? Status(ushort netId) => _statuses.TryGetValue(netId, out var info) ? info : null;

        /// <summary>
        /// The ground shapes of an action (telegraphs, spec 9.3): one per <c>HitShapeDef</c> row of its strikes, and a lane
        /// for each projectile it fires. Empty for an unknown action or one that hits nothing.
        /// </summary>
        public TelegraphShape[] Shapes(int actionNetId) =>
            _shapesByActionNetId.TryGetValue(actionNetId, out var shapes) ? shapes : Array.Empty<TelegraphShape>();

        public static HudConfig From(ConfigSet set)
        {
            var config = new HudConfig(set.Single<HudSettings>(), set.Single<HudThemeTokens>(), Rows<HudLayout>(set),
                new UiText(Rows<UiStrings>(set)), new ReasonText(Rows<ReasonCodeStrings>(set)), ActionCatalog.From(set), set.Single<ChatSettings>());
            config.LoadCombat(set);
            return config;
        }

        /// <summary>
        /// Dev hot reload (spec 11.7): the action, monster, status and telegraph shape lookups from the reloaded tables the
        /// server accepted. Layout, theme and text are not reloaded.
        /// </summary>
        public void RetuneCombat(ConfigSet set, ActionCatalog actions)
        {
            Actions = actions;
            _monsters.Clear();
            _statuses.Clear();
            _shapesByActionNetId.Clear();
            LoadCombat(set);
        }

        private void LoadCombat(ConfigSet set)
        {
            foreach (var monster in Rows<MonsterDef>(set))
                _monsters[monster.NetId] = monster;
            var icons = new Dictionary<string, BuffIconRegistry>(StringComparer.Ordinal);
            foreach (var icon in Rows<BuffIconRegistry>(set))
                icons[icon.Id] = icon;
            foreach (var status in Rows<StatusEffectDef>(set))
            {
                icons.TryGetValue(status.Id, out var icon);
                _statuses[status.NetId] = new StatusInfo(status, icon);
            }
            BuildShapes(Rows<ActionDef>(set), Rows<ActionEffectDef>(set), Rows<HitShapeDef>(set), Rows<ProjectileDef>(set));
        }

        private void BuildShapes(List<ActionDef> actions, List<ActionEffectDef> effects, List<HitShapeDef> hitShapes,
            List<ProjectileDef> projectiles)
        {
            var projectileById = new Dictionary<string, ProjectileDef>(StringComparer.Ordinal);
            foreach (var projectile in projectiles)
                projectileById[projectile.Id] = projectile;
            var shapes = new List<TelegraphShape>();
            foreach (var action in actions)
            {
                shapes.Clear();
                foreach (var effect in effects)
                {
                    if (effect.ActionId != action.Id)
                        continue;
                    if (effect.Kind == EffectKind.Strike)
                    {
                        foreach (var shape in hitShapes)
                        {
                            if (shape.EffectId == effect.Id)
                                shapes.Add(TelegraphShape.From(shape));
                        }
                    }
                    else if (effect.Kind == EffectKind.Projectile && projectileById.TryGetValue(effect.ProjectileId, out var projectile))
                    {
                        shapes.Add(TelegraphShape.Lane(projectile.RangeCm, projectile.HitRadiusCm));
                    }
                }
                if (shapes.Count > 0)
                    _shapesByActionNetId[action.NetId] = shapes.ToArray();
            }
        }

        private static List<T> Rows<T>(ConfigSet set)
        {
            var rows = new List<T>();
            if (set.TryGet(typeof(T), out var table))
            {
                foreach (var row in table.Rows)
                    rows.Add((T)row);
            }
            return rows;
        }
    }

    /// <summary>A status definition with its <c>BuffIconRegistry</c> row (null if the validator was skipped).</summary>
    public sealed class StatusInfo
    {
        public StatusInfo(StatusEffectDef def, BuffIconRegistry? icon)
        {
            Def = def;
            Icon = icon;
        }

        public StatusEffectDef Def { get; }

        public BuffIconRegistry? Icon { get; }

        public int SortOrder => Icon?.SortOrder ?? int.MaxValue;
    }

    /// <summary>
    /// One ground shape of a telegraph in the attacker's frame (cm, forward along the facing, side to its right), the same
    /// geometry as <c>Shared.Simulation.Combat.HitShape</c>: a cone (apex at the origin), a box or lane (from the origin
    /// forward, centered across), a capsule (radius around a forward segment; length 0 is a circle).
    /// </summary>
    public readonly struct TelegraphShape
    {
        private TelegraphShape(HitShapeKind kind, int forwardCm, int sideCm, int reachCm, int breadthCm, int halfAngleDeg)
        {
            Kind = kind;
            ForwardCm = forwardCm;
            SideCm = sideCm;
            ReachCm = reachCm;
            BreadthCm = breadthCm;
            HalfAngleDeg = halfAngleDeg;
        }

        public HitShapeKind Kind { get; }
        public int ForwardCm { get; }
        public int SideCm { get; }

        /// <summary>Cone range, box length, capsule length.</summary>
        public int ReachCm { get; }

        /// <summary>Box half width, capsule radius; 0 for a cone.</summary>
        public int BreadthCm { get; }

        public int HalfAngleDeg { get; }

        public static TelegraphShape From(HitShapeDef row) => row.Kind switch
        {
            HitShapeKind.Cone => new TelegraphShape(HitShapeKind.Cone, row.ForwardOffsetCm, row.SideOffsetCm, row.RangeCm, 0, row.HalfAngleDeg),
            HitShapeKind.Box => new TelegraphShape(HitShapeKind.Box, row.ForwardOffsetCm, row.SideOffsetCm, row.LengthCm, row.WidthCm / 2, 0),
            _ => new TelegraphShape(HitShapeKind.Capsule, row.ForwardOffsetCm, row.SideOffsetCm, row.LengthCm, row.RadiusCm, 0),
        };

        /// <summary>A projectile's path: <paramref name="rangeCm"/> forward, its hit radius each side.</summary>
        public static TelegraphShape Lane(int rangeCm, int hitRadiusCm) =>
            new TelegraphShape(HitShapeKind.Box, 0, 0, rangeCm, hitRadiusCm, 0);
    }

    /// <summary><see cref="HudThemeTokens"/> as Unity colors (spec 9.7).</summary>
    public sealed class HudTheme
    {
        public HudTheme(HudThemeTokens tokens)
        {
            Tokens = tokens;
            Panel = ColorOf(tokens.Panel);
            Border = ColorOf(tokens.Border);
            Text = ColorOf(tokens.Text);
            Accent = ColorOf(tokens.Accent);
            Hp = ColorOf(tokens.Hp);
            Mp = ColorOf(tokens.Mp);
            Stamina = ColorOf(tokens.Stamina);
            Parry = ColorOf(tokens.Parry);
            Dodge = ColorOf(tokens.Dodge);
            BuffBorder = ColorOf(tokens.BuffBorder);
            DebuffBorder = ColorOf(tokens.DebuffBorder);
            Locked = ColorOf(tokens.Locked);
            DamageText = ColorOf(tokens.DamageText);
            CritText = ColorOf(tokens.CritText);
            HealText = ColorOf(tokens.HealText);
            TelegraphRed = ColorOf(tokens.TelegraphRed);
            TelegraphYellow = ColorOf(tokens.TelegraphYellow);
            TelegraphBlue = ColorOf(tokens.TelegraphBlue);
            TelegraphGreen = ColorOf(tokens.TelegraphGreen);
            TelegraphNone = ColorOf(tokens.TelegraphNone);
            Staggered = ColorOf(tokens.Staggered);
            ParryFlash = ColorOf(tokens.ParryFlash);
            HitFlash = ColorOf(tokens.HitFlash);
            Exp = ColorOf(tokens.Exp);
        }

        public HudThemeTokens Tokens { get; }
        public Color Panel { get; }
        public Color Border { get; }
        public Color Text { get; }
        public Color Accent { get; }
        public Color Hp { get; }
        public Color Mp { get; }
        public Color Stamina { get; }
        public Color Parry { get; }
        public Color Dodge { get; }
        public Color BuffBorder { get; }
        public Color DebuffBorder { get; }
        public Color Locked { get; }
        public Color DamageText { get; }
        public Color CritText { get; }
        public Color HealText { get; }
        public Color TelegraphRed { get; }
        public Color TelegraphYellow { get; }
        public Color TelegraphBlue { get; }
        public Color TelegraphGreen { get; }
        public Color TelegraphNone { get; }

        /// <summary>The stagger marker on a monster's name plate (R-3.3-01, spec 9.3 <c>stagger_marker</c>).</summary>
        public Color Staggered { get; }

        /// <summary>The P square on a successful parry, and the tint of the monster whose hit was parried (fix/parry-feel).</summary>
        public Color ParryFlash { get; }
        public Color HitFlash { get; }

        /// <summary>The exp bar fill (spec 9.3 <c>exp_bar</c>, WP-M2-3).</summary>
        public Color Exp { get; }

        public Color Telegraph(ParryColor color) => color switch
        {
            ParryColor.Red => TelegraphRed,
            ParryColor.Yellow => TelegraphYellow,
            ParryColor.Blue => TelegraphBlue,
            ParryColor.Green => TelegraphGreen,
            _ => TelegraphNone,
        };

        /// <summary>A validated token; magenta for a bad one so it stands out (the DataValidator refuses those).</summary>
        public static Color ColorOf(string token) =>
            HudColor.TryParse(token, out var c) ? new Color32(c.R, c.G, c.B, c.A) : Color.magenta;
    }
}
