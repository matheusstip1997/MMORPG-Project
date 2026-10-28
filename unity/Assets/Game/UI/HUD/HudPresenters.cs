#nullable enable
using System;
using System.Collections.Generic;
using Game.UI.Config;
using Game.UI.ViewModels.Hud;
using Shared.Data.Tables;
using Shared.Protocol;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace Game.UI.HUD
{
    /// <summary>The spec 9.3 element ids HUD v1 places (backlog I) and the inventory window (WP16); each has a <c>HudLayout</c> row.</summary>
    public static class HudElementIds
    {
        public const string ResourcePanel = "resource_panel";
        public const string BossBar = "boss_bar";
        public const string Chat = "chat";
        public const string ActionToast = "action_toast";
        public const string CastBar = "cast_bar";
        public const string BuffRow = "buff_row";
        public const string ParryDodge = "parry_dodge";
        public const string HotbarLeft = "hotbar_left";
        public const string Gauge = "gauge";
        public const string HotbarRight = "hotbar_right";
        /// <summary>The inventory window (spec 9.13, WP16; <c>Game.UI.Menus.Inventory</c>).</summary>
        public const string InventoryWindow = "inventory_window";
        /// <summary>The pickup prompt at a ground drop (backlog G; <c>Overlays/PickupPromptOverlay</c>): world-anchored.</summary>
        public const string PickupPrompt = "pickup_prompt";
        /// <summary>The stagger marker over a staggered monster's name plate (R-3.3-01; <c>Overlays/NamePlateOverlay</c>): world-anchored.</summary>
        public const string StaggerMarker = "stagger_marker";
        /// <summary>The committed gold under the resource panel (WP-M2-4; <c>HUD/GoldPresenter</c>).</summary>
        public const string Gold = "gold";
        /// <summary>The outpost sale window (spec 9.13 M2, WP-M2-4; <c>Game.UI.Menus.Outpost</c>).</summary>
        public const string OutpostWindow = "outpost_window";
        /// <summary>The NPC vendor window (spec 9.13 M2, WP-M2-4).</summary>
        public const string VendorWindow = "vendor_window";
        /// <summary>The NPC prompt at the NPC in reach (WP-M2-4; <c>Overlays/NpcPromptOverlay</c>): world-anchored.</summary>
        public const string NpcPrompt = "npc_prompt";
        /// <summary>The exp bar under the hotbar (spec 9.3, WP-M2-3; <see cref="ExpBarPresenter"/>).</summary>
        public const string ExpBar = "exp_bar";
        /// <summary>The exp multiplier note above the exp bar while the band modifier is below 1 (R-3.6-04).</summary>
        public const string ExpNote = "exp_note";
        /// <summary>The skill tree window (spec 3.5, 9.13 pattern; WP-M2-5; <c>Game.UI.Menus.Skills</c>).</summary>
        public const string SkillTreeWindow = "skill_tree_window";
        /// <summary>The Bystial book window (spec 14.2, 9.13 M2, WP-M2-8; <c>Game.UI.Menus.Book</c>).</summary>
        public const string BookWindow = "book_window";
        /// <summary>The quest board and run builder at an outpost (spec 9.13 M2, WP-M2-6; <c>Game.UI.Menus.Quests</c>).</summary>
        public const string QuestBoard = "quest_board";
        /// <summary>The run tracker (spec 9.3, WP-M2-6; <see cref="RunTrackerPresenter"/>).</summary>
        public const string RunTracker = "run_tracker";

        public static readonly string[] All =
        {
            ResourcePanel, BossBar, Chat, ActionToast, CastBar, BuffRow, ParryDodge, HotbarLeft, Gauge, HotbarRight, InventoryWindow,
            PickupPrompt, StaggerMarker, Gold, OutpostWindow, VendorWindow, NpcPrompt, ExpBar, ExpNote, SkillTreeWindow, BookWindow,
            QuestBoard, RunTracker,
        };

        /// <summary>
        /// Elements whose layout row is measured from a projected world point instead of the screen: the point is a zero-size
        /// box, so the anchor and offset place the element around it (bottomCenter with offset [0, y]: centered, y px above).
        /// </summary>
        public static readonly string[] WorldAnchored = { PickupPrompt, StaggerMarker, NpcPrompt };
    }

    /// <summary>
    /// Spec 9.3 <c>resource_panel</c>: a level and rank line (WP-M2-3, until the character panel shows rank), HP, MP and
    /// stamina bars with "current / max", then TOX and SAT side by side on a fourth row (R-3.4-03, WP-M2-2; half width each
    /// so the panel keeps its 9.3 size). Hovering the panel shows the stats readout: Atk, Matk, Def, MDef and STR, DEX, STA,
    /// INT, all from the server's own-stats message (the character panel with every stat is WP-M2-11).
    /// </summary>
    public sealed class ResourcePanelPresenter : HudPresenter
    {
        public const int Toxicity = 3;
        public const int Satiety = 4;

        private readonly ResourcePanelViewModel _model;
        private readonly ProgressViewModel? _progress;
        private Label? _progressLine;
        private readonly VisualElement[] _fills = new VisualElement[5];
        private readonly Label[] _values = new Label[5];
        private VisualElement _tooltip = null!;
        private Label _combatLine = null!;
        private Label _attributeLine = null!;

        public ResourcePanelPresenter(HudConfig config, HudView view, ResourcePanelViewModel model, ProgressViewModel? progress = null)
            : base(config, view, progress == null ? new Game.UI.ViewModels.ViewModel[] { model } : new Game.UI.ViewModels.ViewModel[] { model, progress })
        {
            _model = model;
            _progress = progress;
        }

        /// <summary>The stats readout shown on hover.</summary>
        public VisualElement Tooltip => _tooltip;

        protected override void Build()
        {
            var panel = View.Place(HudElementIds.ResourcePanel, HudWidgets.Panel("hud-resources", Theme));
            panel.pickingMode = PickingMode.Position; // hover shows the stats readout
            panel.Add(Row(0, HudTextIds.LabelHp, Theme.Hp, "hud-resources__row"));
            panel.Add(Row(1, HudTextIds.LabelMp, Theme.Mp, "hud-resources__row"));
            panel.Add(Row(2, HudTextIds.LabelStamina, Theme.Stamina, "hud-resources__row"));
            var meters = HudWidgets.Box("hud-resources__row");
            meters.Add(Row(Toxicity, HudTextIds.LabelToxicity, Theme.Toxicity, "hud-resources__half"));
            meters.Add(Row(Satiety, HudTextIds.LabelSatiety, Theme.Satiety, "hud-resources__half"));
            panel.Add(meters);

            _tooltip = HudWidgets.Box("hud-tooltip");
            _tooltip.AddToClassList("hud-tooltip--below");
            _tooltip.style.backgroundColor = Theme.Panel;
            HudWidgets.Border(_tooltip, Theme.Border);
            _tooltip.Add(HudWidgets.Label(Config.Text.Get(HudTextIds.StatsTitle), "hud-tooltip__title"));
            _combatLine = HudWidgets.Label("", "hud-tooltip__line");
            _attributeLine = HudWidgets.Label("", "hud-tooltip__line");
            _tooltip.Add(_combatLine);
            _tooltip.Add(_attributeLine);
            Show(_tooltip, false);
            panel.Add(_tooltip);
            panel.RegisterCallback<PointerEnterEvent>(_ => Show(_tooltip, _model.HasStats));
            panel.RegisterCallback<PointerLeaveEvent>(_ => Show(_tooltip, false));
            BuildProgressLine(panel);
        }

        private VisualElement Row(int index, string labelId, Color color, string className)
        {
            var row = HudWidgets.Box(className);
            row.Add(HudWidgets.Label(Config.Text.Get(labelId), "hud-resources__label"));
            var bar = HudWidgets.Bar("hud-resources__bar", color, out _fills[index]);
            _values[index] = HudWidgets.Label("", "hud-resources__value");
            bar.Add(_values[index]);
            row.Add(bar);
            return row;
        }

        protected override void Refresh()
        {
            SetRow(0, _model.HasValues, _model.Hp, _model.MaxHp);
            SetRow(1, _model.HasValues, _model.Mp, _model.MaxMp);
            SetRow(2, _model.HasValues, _model.Stamina, _model.MaxStamina);
            SetRow(Toxicity, _model.HasStats, _model.Toxicity, _model.ToxicityMax);
            SetRow(Satiety, _model.HasStats, _model.Satiety, _model.SatietyMax);
            var stats = _model.Stats;
            HudWidgets.SetText(_combatLine, _model.HasStats
                ? Config.Text.Get(HudTextIds.StatsCombat, new TextArg("atk", NumberText.Of(stats.Atk)),
                    new TextArg("matk", NumberText.Of(stats.Matk)), new TextArg("def", NumberText.Of(stats.Def)),
                    new TextArg("mdef", NumberText.Of(stats.Mdef)))
                : "");
            HudWidgets.SetText(_attributeLine, _model.HasStats
                ? Config.Text.Get(HudTextIds.StatsAttributes, new TextArg("str", NumberText.Of(stats.Str)),
                    new TextArg("dex", NumberText.Of(stats.Dex)), new TextArg("sta", NumberText.Of(stats.Sta)),
                    new TextArg("int", NumberText.Of(stats.Int)))
                : "");
            if (!_model.HasStats)
                Show(_tooltip, false);
            RefreshProgressLine();
        }

        // WP-M2-3: the level and rank line (spec 3.6), its own block so the TOX and SAT rows (WP-M2-2) stay apart.
        private void BuildProgressLine(VisualElement panel)
        {
            if (_progress == null)
                return;
            _progressLine = HudWidgets.Label("", "hud-resources__progress");
            panel.Insert(0, _progressLine);
        }

        private void RefreshProgressLine()
        {
            if (_progress == null || _progressLine == null)
                return;
            HudWidgets.SetText(_progressLine, _progress.HasValues
                ? Config.Text.Get(HudTextIds.ProgressLine, new TextArg("level", NumberText.Of(_progress.Level)),
                    new TextArg("rank", Config.Text.Get(UiStringCoverage.RankId(_progress.Rank))))
                : "");
        }

        /// <summary>The level and rank line, for tests and QA checks; empty without progression.</summary>
        public string ProgressText => _progressLine?.text ?? "";
        // End WP-M2-3.

        private void SetRow(int index, bool hasValues, int value, int max)
        {
            HudWidgets.SetFill(_fills[index], hasValues ? ResourcePanelViewModel.Fraction(value, max) : 0f);
            HudWidgets.SetText(_values[index], hasValues
                ? Config.Text.Get(HudTextIds.ResourceValue, new TextArg("current", NumberText.Of(value)), new TextArg("max", NumberText.Of(max)))
                : "");
        }

        /// <summary>The value text of a row (0 HP, 1 MP, 2 stamina, 3 toxicity, 4 satiety), for tests and QA checks.</summary>
        public string ValueText(int index) => _values[index].text;

        /// <summary>The two readout lines (combat stats, attributes), for tests.</summary>
        public string ReadoutText => _combatLine.text + "\n" + _attributeLine.text;
    }

    /// <summary>
    /// Spec 9.3 <c>hotbar_left</c> / <c>hotbar_right</c> (9.6): the slots of one side, by the skill slot numbers of its
    /// <c>HudLayout.slots</c>, each with its key, the action's name (text until hotbar icons exist), a cooldown overlay
    /// and seconds left, and a flash when a press was refused. 0 is the unassigned slot.
    /// </summary>
    public sealed class HotbarPresenter : HudPresenter
    {
        private readonly HotbarViewModel _model;
        private readonly string _elementId;
        private SlotElements[] _slots = Array.Empty<SlotElements>();

        public HotbarPresenter(HudConfig config, HudView view, HotbarViewModel model, string elementId) : base(config, view, model)
        {
            _model = model;
            _elementId = elementId;
        }

        private sealed class SlotElements
        {
            public int SkillSlot;
            public VisualElement Root = null!;
            public Label Name = null!;
            public VisualElement Cooldown = null!;
            public Label Seconds = null!;
            public int ShownSeconds = -1;
            public bool Flashing;

            /// <summary>The action's full name and its optional short name (UiStrings); the label shows what fits.</summary>
            public string FullText = "";
            public string ShortText = "";

            /// <summary>The label could not be measured yet (no layout or font): fitted again on the next frame.</summary>
            public bool FitPending;
        }

        /// <summary>
        /// The text of a slot label at the fixed slot label size (QA wave 6 F5, spec 9.7/9.8: never smaller than the
        /// <c>fontSizeSlotLabel</c> token; QA wave 5 V2: never a break inside a word). The <paramref name="full"/> name
        /// wrapped at word boundaries when every line fits <paramref name="available"/> and there are at most
        /// <paramref name="maxLines"/>; else the <paramref name="shortName"/> (UiStrings, optional) wrapped the same way; else
        /// the shorter of the two with each word that is too wide cut and ended with <paramref name="ellipsis"/>, and the last
        /// line ended with it when there are too many. Lines are joined with '\n'. A pure function of its inputs, so the label
        /// is the same in every run.
        /// </summary>
        public static string FitLabel(string full, string shortName, Func<string, float> width, float available, int maxLines,
            string ellipsis)
        {
            if (full.Length == 0 || available <= 0f || maxLines <= 0)
                return full;
            if (TryWrap(full, width, available, maxLines, out var wrapped))
                return wrapped;
            string text = full;
            if (shortName.Length > 0)
            {
                if (TryWrap(shortName, width, available, maxLines, out wrapped))
                    return wrapped;
                text = shortName;
            }
            var lines = Wrap(text, width, available, word => Cut(word, width, available, ellipsis));
            if (lines.Count > maxLines)
            {
                lines.RemoveRange(maxLines, lines.Count - maxLines);
                string last = lines[maxLines - 1];
                if (!last.EndsWith(ellipsis, StringComparison.Ordinal))
                    lines[maxLines - 1] = width(last + ellipsis) <= available ? last + ellipsis : Cut(last, width, available, ellipsis);
            }
            return string.Join("\n", lines);
        }

        private static bool TryWrap(string text, Func<string, float> width, float available, int maxLines, out string wrapped)
        {
            bool cut = false;
            var lines = Wrap(text, width, available, word =>
            {
                cut = true;
                return word;
            });
            wrapped = string.Join("\n", lines);
            return !cut && lines.Count <= maxLines;
        }

        /// <summary>Greedy wrap at spaces; a word wider than a line goes through <paramref name="tooWide"/>.</summary>
        private static List<string> Wrap(string text, Func<string, float> width, float available, Func<string, string> tooWide)
        {
            var lines = new List<string>();
            string line = "";
            foreach (var raw in text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string word = width(raw) > available ? tooWide(raw) : raw;
                string joined = line.Length == 0 ? word : line + " " + word;
                if (line.Length == 0 || width(joined) <= available)
                {
                    line = joined;
                    continue;
                }
                lines.Add(line);
                line = word;
            }
            if (line.Length > 0)
                lines.Add(line);
            return lines;
        }

        /// <summary>The longest start of <paramref name="text"/> that fits with <paramref name="ellipsis"/> after it.</summary>
        private static string Cut(string text, Func<string, float> width, float available, string ellipsis)
        {
            for (int length = text.Length - 1; length > 0; length--)
            {
                string candidate = text.Substring(0, length).TrimEnd() + ellipsis;
                if (width(candidate) <= available)
                    return candidate;
            }
            return ellipsis;
        }

        private void FitName(SlotElements slot)
        {
            slot.FitPending = false;
            if (slot.FullText.Length == 0)
            {
                HudWidgets.SetText(slot.Name, "");
                return;
            }
            // The label's own margins and padding come off the slot's content box; 1 px covers measuring versus layout
            // rounding (smoke fix5d: "Flamethrowe / r" without it).
            var label = slot.Name.resolvedStyle;
            var box = slot.Root.contentRect;
            float available = box.width - label.marginLeft - label.marginRight - label.paddingLeft - label.paddingRight
                              - label.borderLeftWidth - label.borderRightWidth - 1f;
            float height = box.height - label.marginTop - label.marginBottom - label.paddingTop - label.paddingBottom;
            float lineHeight = Measure(slot.Name, "Ag").y;
            if (float.IsNaN(available) || available <= 0f || float.IsNaN(height) || float.IsNaN(lineHeight) || lineHeight <= 0f)
            {
                // Not laid out or no font yet: the full name for now, fitted once it can be measured.
                HudWidgets.SetText(slot.Name, slot.FullText);
                slot.FitPending = true;
                return;
            }
            int maxLines = Math.Max(1, (int)Math.Floor(height / lineHeight));
            HudWidgets.SetText(slot.Name, FitLabel(slot.FullText, slot.ShortText, text => Measure(slot.Name, text).x, available,
                maxLines, Config.Text.Get(HudTextIds.SlotLabelEllipsis)));
        }

        private static Vector2 Measure(Label label, string text) =>
            label.MeasureTextSize(text, 0, VisualElement.MeasureMode.Undefined, 0, VisualElement.MeasureMode.Undefined);

        protected override void Build()
        {
            var grid = View.Place(_elementId, HudWidgets.Box("hud-hotbar"));
            int[] numbers = Config.TryGetLayout(_elementId, out var row) ? row.Slots : Array.Empty<int>();
            _slots = new SlotElements[numbers.Length];
            for (int i = 0; i < numbers.Length; i++)
            {
                var slot = new SlotElements { SkillSlot = numbers[i], Root = HudWidgets.Box("hud-slot") };
                slot.Root.name = "slot_" + numbers[i];
                slot.Root.style.backgroundColor = Theme.Panel;
                HudWidgets.Border(slot.Root, numbers[i] == 0 ? Theme.Locked : Theme.Border);
                if (numbers[i] == 0)
                    slot.Root.AddToClassList("hud-slot--unassigned"); // spec 9.3: drawn dashed (a faint frame here)
                slot.Cooldown = HudWidgets.Box("hud-slot__cooldown");
                slot.Root.Add(slot.Cooldown);
                slot.Name = HudWidgets.Label("", "hud-slot__name");
                // F5: always the token size (spec 9.7); a long name is shortened instead (FitLabel), never shrunk.
                slot.Name.style.fontSize = Theme.Tokens.FontSizeSlotLabel;
                slot.Root.Add(slot.Name);
                if (numbers[i] != 0)
                {
                    var key = HudWidgets.Label(Config.Text.Get(HudTextIds.KeySlot(numbers[i])), "hud-slot__key");
                    key.style.fontSize = Theme.Tokens.FontSizeSlotLabel;
                    slot.Root.Add(key);
                }
                slot.Seconds = HudWidgets.Label("", "hud-slot__seconds");
                slot.Root.Add(slot.Seconds);
                slot.Root.RegisterCallback<GeometryChangedEvent>(_ => FitName(slot));
                grid.Add(slot.Root);
                _slots[i] = slot;
            }
        }

        protected override void Refresh()
        {
            foreach (var slot in _slots)
            {
                var model = _model.Slot(slot.SkillSlot);
                bool empty = model == null || model.IsEmpty;
                string full = empty ? "" : Config.Text.Get(UiStringCoverage.ActionId(model!.ActionId));
                string shortId = empty ? "" : HudTextIds.ActionShort(model!.ActionId);
                string shortName = empty || !Config.Text.Has(shortId) ? "" : Config.Text.Get(shortId);
                if (full != slot.FullText || shortName != slot.ShortText)
                {
                    slot.FullText = full;
                    slot.ShortText = shortName;
                    FitName(slot);
                }
                slot.Root.EnableInClassList("hud-slot--empty", empty);
                // R-3.5-07 (WP-M2-5): a learned skill whose weapon is not equipped stays on the bar, greyed.
                slot.Root.EnableInClassList("hud-slot--unusable", !empty && model!.Unusable);
            }
        }

        public override void Animate(double nowMs)
        {
            foreach (var slot in _slots)
            {
                if (slot.FitPending)
                    FitName(slot);
                var model = _model.Slot(slot.SkillSlot);
                if (model == null)
                    continue;
                float fraction = model.CooldownFraction(nowMs);
                slot.Cooldown.style.height = Length.Percent(fraction * 100f);
                int seconds = fraction > 0f ? (int)Math.Ceiling(model.CooldownLeftMs(nowMs) / 1000.0) : 0;
                if (seconds != slot.ShownSeconds)
                {
                    slot.ShownSeconds = seconds;
                    slot.Seconds.text = seconds > 0
                        ? Config.Text.Get(HudTextIds.CooldownSeconds, new TextArg("seconds", NumberText.Of(seconds)))
                        : "";
                    // QA wave 5 V1: the number takes the label's place while the slot cools down ("Pie6ing Attack").
                    slot.Root.EnableInClassList("hud-slot--cooling", seconds > 0);
                }
                bool flashing = model.FlashUntilMs > nowMs;
                if (flashing != slot.Flashing)
                {
                    slot.Flashing = flashing;
                    HudWidgets.Border(slot.Root, flashing ? Theme.Hp : slot.SkillSlot == 0 ? Theme.Locked : Theme.Border);
                    slot.Root.EnableInClassList("hud-slot--flash", flashing);
                }
            }
        }

        /// <summary>
        /// The skill slot under a panel position (WP-M2-5: the skill tree window drops a learned skill there), or -1 when no slot of
        /// this side is under it. The unassigned slot 0 takes no skill.
        /// </summary>
        public int SlotAt(Vector2 panelPosition)
        {
            foreach (var slot in _slots)
            {
                if (slot.SkillSlot != 0 && slot.Root.worldBound.Contains(panelPosition))
                    return slot.SkillSlot;
            }
            return -1;
        }

        /// <summary>Whether a skill slot is drawn greyed for its weapon (tests), or null when this side has no such slot.</summary>
        public bool? SlotUnusable(int skillSlot)
        {
            foreach (var slot in _slots)
            {
                if (slot.SkillSlot == skillSlot)
                    return slot.Root.ClassListContains("hud-slot--unusable");
            }
            return null;
        }

        /// <summary>The cooldown seconds shown on a skill slot and whether its name is hidden for them (tests).</summary>
        public (string Seconds, bool NameHidden)? SlotCooldown(int skillSlot)
        {
            foreach (var slot in _slots)
            {
                if (slot.SkillSlot == skillSlot)
                    return (slot.Seconds.text, slot.Root.ClassListContains("hud-slot--cooling"));
            }
            return null;
        }

        /// <summary>The name shown on a skill slot (tests and QA checks), or null when this side has no such slot.</summary>
        public string? SlotText(int skillSlot)
        {
            foreach (var slot in _slots)
            {
                if (slot.SkillSlot == skillSlot)
                    return slot.Name.text;
            }
            return null;
        }
    }

    /// <summary>
    /// Spec 9.3 <c>gauge</c>: the class gauge is a locked, greyed frame in M1 (design §3, backlog E): 12 empty segments, a
    /// center marker and a lock, with a tooltip on hover. No <c>ClassGauge</c> system yet.
    /// </summary>
    public sealed class GaugePresenter : HudPresenter
    {
        public const int Segments = 12;

        private Label _tooltip = null!;

        public GaugePresenter(HudConfig config, HudView view) : base(config, view)
        {
        }

        public Label Tooltip => _tooltip;

        protected override void Build()
        {
            var frame = View.Place(HudElementIds.Gauge, HudWidgets.Panel("hud-gauge", Theme));
            frame.pickingMode = PickingMode.Position; // hover shows the tooltip
            frame.AddToClassList("hud-gauge--locked");
            var segments = HudWidgets.Box("hud-gauge__segments");
            for (int i = 0; i < Segments; i++)
            {
                if (i == Segments / 2)
                {
                    var marker = HudWidgets.Box("hud-gauge__marker");
                    marker.style.backgroundColor = Theme.Locked;
                    segments.Add(marker);
                }
                var segment = HudWidgets.Box("hud-gauge__segment");
                HudWidgets.Border(segment, Theme.Locked);
                segments.Add(segment);
            }
            frame.Add(segments);
            var lockIcon = new LockIcon(Theme.Locked) { name = "gauge_lock" };
            frame.Add(lockIcon);
            _tooltip = HudWidgets.Label(Config.Text.Get(HudTextIds.GaugeLocked), "hud-tooltip");
            _tooltip.style.backgroundColor = Theme.Panel;
            HudWidgets.Border(_tooltip, Theme.Border);
            Show(_tooltip, false);
            frame.Add(_tooltip);
            frame.RegisterCallback<PointerEnterEvent>(_ => Show(_tooltip, true));
            frame.RegisterCallback<PointerLeaveEvent>(_ => Show(_tooltip, false));
        }

        protected override void Refresh()
        {
        }

        /// <summary>A padlock drawn with vector paths (no art needed).</summary>
        private sealed class LockIcon : VisualElement
        {
            private readonly Color _color;

            public LockIcon(Color color)
            {
                _color = color;
                pickingMode = PickingMode.Ignore;
                AddToClassList("hud-gauge__lock");
                generateVisualContent += Draw;
            }

            private void Draw(MeshGenerationContext context)
            {
                var rect = contentRect;
                if (rect.width <= 0f || rect.height <= 0f)
                    return;
                var paint = context.painter2D;
                float w = rect.width, h = rect.height;
                paint.strokeColor = _color;
                paint.fillColor = _color;
                paint.lineWidth = Mathf.Max(1.5f, w * 0.12f);
                paint.BeginPath();
                paint.Arc(new Vector2(w * 0.5f, h * 0.45f), w * 0.28f, 180f, 360f);
                paint.LineTo(new Vector2(w * 0.78f, h * 0.55f));
                paint.MoveTo(new Vector2(w * 0.22f, h * 0.45f));
                paint.LineTo(new Vector2(w * 0.22f, h * 0.55f));
                paint.Stroke();
                paint.BeginPath();
                paint.MoveTo(new Vector2(w * 0.1f, h * 0.55f));
                paint.LineTo(new Vector2(w * 0.9f, h * 0.55f));
                paint.LineTo(new Vector2(w * 0.9f, h * 0.98f));
                paint.LineTo(new Vector2(w * 0.1f, h * 0.98f));
                paint.ClosePath();
                paint.Fill();
            }
        }
    }

    /// <summary>
    /// Spec 9.3 <c>parry_dodge</c>: one square each with the stack count, P (parry color) and D (dodge color), a recharge
    /// fill computed from timestamps, and the key (spec 9.6 defaults: right click, Ctrl).
    /// </summary>
    public sealed class ParryDodgePresenter : HudPresenter
    {
        private readonly ChargesViewModel _model;
        private Label _parryCount = null!;
        private Label _dodgeCount = null!;
        private VisualElement _parryFill = null!;
        private VisualElement _dodgeFill = null!;
        private VisualElement _parrySquare = null!;

        public ParryDodgePresenter(HudConfig config, HudView view, ChargesViewModel model) : base(config, view, model)
        {
            _model = model;
        }

        protected override void Build()
        {
            var column = View.Place(HudElementIds.ParryDodge, HudWidgets.Box("hud-charges"));
            _parrySquare = Square("parry", HudTextIds.Parry, HudTextIds.KeyParry, Theme.Parry, out _parryCount, out _parryFill);
            column.Add(_parrySquare);
            column.Add(Square("dodge", HudTextIds.Dodge, HudTextIds.KeyDodge, Theme.Dodge, out _dodgeCount, out _dodgeFill));
        }

        private VisualElement Square(string name, string letterId, string keyId, Color color, out Label count, out VisualElement fill)
        {
            var square = HudWidgets.Box("hud-charge");
            square.name = name;
            square.style.backgroundColor = Theme.Panel;
            HudWidgets.Border(square, color);
            var letter = HudWidgets.Label(Config.Text.Get(letterId), "hud-charge__letter");
            letter.style.color = color;
            square.Add(letter);
            count = HudWidgets.Label("", "hud-charge__count");
            count.style.fontSize = Theme.Tokens.FontSizeStack;
            square.Add(count);
            var key = HudWidgets.Label(Config.Text.Get(keyId), "hud-charge__key");
            key.style.fontSize = Theme.Tokens.FontSizeSlotLabel;
            square.Add(key);
            var track = HudWidgets.Bar("hud-charge__recharge", color, out fill);
            square.Add(track);
            return square;
        }

        protected override void Refresh()
        {
            HudWidgets.SetText(_parryCount, _model.HasValues ? NumberText.Of(_model.Parry.Count) : "");
            HudWidgets.SetText(_dodgeCount, _model.HasValues ? NumberText.Of(_model.Dodge.Count) : "");
        }

        public override void Animate(double nowMs)
        {
            HudWidgets.SetFill(_parryFill, _model.HasValues ? _model.Parry.Progress(nowMs) : 0f);
            HudWidgets.SetFill(_dodgeFill, _model.HasValues ? _model.Dodge.Progress(nowMs) : 0f);
            // A successful parry fills the P square in the parryFlash token for HudSettings.parryFlashMs (fix/parry-feel).
            bool flashing = _model.ParryFlashing(nowMs);
            if (flashing != ParryFlashing)
            {
                ParryFlashing = flashing;
                _parrySquare.style.backgroundColor = flashing ? Theme.ParryFlash : Theme.Panel;
            }
        }

        /// <summary>The P square shows the success flash now (QA checks).</summary>
        public bool ParryFlashing { get; private set; }

        public string ParryText => _parryCount.text;

        public string DodgeText => _dodgeCount.text;
    }

    /// <summary>
    /// Spec 9.3 <c>buff_row</c>: buffs (buff border) and debuffs (debuff border) of the own snapshot, by sort order, then the
    /// armed, mounted and pet chips (spec 9.5: AR, MT, PT placeholders). An empty <c>iconAddress</c> shows the status name.
    /// TODO(icons): load <c>BuffIconRegistry.iconAddress</c> through Addressables once icons exist.
    /// </summary>
    public sealed class BuffRowPresenter : HudPresenter
    {
        private const int FlagChips = 3;

        private readonly BuffRowViewModel _model;
        private readonly VisualElement[] _chips = new VisualElement[Snapshot.MaxOwnStatuses + FlagChips];
        private readonly Label[] _names = new Label[Snapshot.MaxOwnStatuses + FlagChips];
        private readonly Label[] _stacks = new Label[Snapshot.MaxOwnStatuses + FlagChips];

        public BuffRowPresenter(HudConfig config, HudView view, BuffRowViewModel model) : base(config, view, model)
        {
            _model = model;
        }

        protected override void Build()
        {
            var row = View.Place(HudElementIds.BuffRow, HudWidgets.Box("hud-buffs"));
            for (int i = 0; i < _chips.Length; i++)
            {
                _chips[i] = HudWidgets.Box("hud-buff");
                _chips[i].style.backgroundColor = Theme.Panel;
                _names[i] = HudWidgets.Label("", "hud-buff__name");
                _stacks[i] = HudWidgets.Label("", "hud-buff__stacks");
                _chips[i].Add(_names[i]);
                _chips[i].Add(_stacks[i]);
                Show(_chips[i], false);
                row.Add(_chips[i]);
            }
        }

        protected override void Refresh()
        {
            int shown = 0;
            for (int i = 0; i < _model.Count; i++)
            {
                ref readonly var buff = ref _model[i];
                // TODO(icons): an icon from buff.IconAddress; the name shows while it is empty (spec 9.7 BuffIconRegistry).
                Chip(shown++, Config.Text.Get(UiStringCoverage.StatusId(buff.StatusId)),
                    buff.Stacks > 1 ? Config.Text.Get(HudTextIds.Stacks, new TextArg("stacks", NumberText.Of(buff.Stacks))) : "",
                    buff.Kind == StatusEffectKind.Debuff ? Theme.DebuffBorder : Theme.BuffBorder);
            }
            var flags = _model.Flags;
            if ((flags & EntityFlags.Armed) != 0)
                Chip(shown++, Config.Text.Get(HudTextIds.FlagArmed), "", Theme.Border);
            if ((flags & EntityFlags.Mounted) != 0)
                Chip(shown++, Config.Text.Get(HudTextIds.FlagMounted), "", Theme.Border);
            if ((flags & EntityFlags.Pet) != 0)
                Chip(shown++, Config.Text.Get(HudTextIds.FlagPet), "", Theme.Border);
            for (int i = shown; i < _chips.Length; i++)
                Show(_chips[i], false);
        }

        private void Chip(int index, string name, string stacks, Color border)
        {
            HudWidgets.SetText(_names[index], name);
            HudWidgets.SetText(_stacks[index], stacks);
            HudWidgets.Border(_chips[index], border);
            Show(_chips[index], true);
        }

        /// <summary>The names on the visible chips, in order (tests).</summary>
        public string[] ChipTexts()
        {
            int count = 0;
            foreach (var chip in _chips)
            {
                if (chip.style.display == DisplayStyle.Flex)
                    count++;
            }
            var texts = new string[count];
            for (int i = 0; i < count; i++)
                texts[i] = _names[i].text;
            return texts;
        }
    }

    /// <summary>Spec 9.3 <c>boss_bar</c>: boss name, rank and an HP bar with no numbers, only while engaged.</summary>
    public sealed class BossBarPresenter : HudPresenter
    {
        private readonly BossBarViewModel _model;
        private VisualElement _panel = null!;
        private Label _title = null!;
        private VisualElement _fill = null!;

        public BossBarPresenter(HudConfig config, HudView view, BossBarViewModel model) : base(config, view, model)
        {
            _model = model;
        }

        protected override void Build()
        {
            _panel = View.Place(HudElementIds.BossBar, HudWidgets.Panel("hud-boss", Theme));
            _title = HudWidgets.Label("", "hud-boss__title");
            _panel.Add(_title);
            _panel.Add(HudWidgets.Bar("hud-boss__bar", Theme.Hp, out _fill));
        }

        protected override void Refresh()
        {
            Show(_panel, _model.Visible);
            if (!_model.Visible)
                return;
            HudWidgets.SetText(_title, Config.Text.Get(HudTextIds.BossTitle,
                new TextArg("name", Config.Text.Get(UiStringCoverage.MonsterId(_model.MonsterId))),
                new TextArg("rank", Config.Text.Get(UiStringCoverage.RankId(_model.Rank)))));
            HudWidgets.SetFill(_fill, _model.HpFraction);
        }

        public bool IsShown => _panel.style.display == DisplayStyle.Flex;
    }

    /// <summary>Spec 9.3 <c>cast_bar</c>: the local player's channel, its name and time left, centered above the hotbar.</summary>
    public sealed class CastBarPresenter : HudPresenter
    {
        private readonly CastBarViewModel _model;
        private VisualElement _panel = null!;
        private Label _name = null!;
        private Label _time = null!;
        private VisualElement _fill = null!;
        private int _shownTenths = -1;

        public CastBarPresenter(HudConfig config, HudView view, CastBarViewModel model) : base(config, view, model)
        {
            _model = model;
        }

        protected override void Build()
        {
            _panel = View.Place(HudElementIds.CastBar, HudWidgets.Panel("hud-cast", Theme));
            var bar = HudWidgets.Bar("hud-cast__bar", Theme.Accent, out _fill);
            _name = HudWidgets.Label("", "hud-cast__name");
            _time = HudWidgets.Label("", "hud-cast__time");
            bar.Add(_name);
            bar.Add(_time);
            _panel.Add(bar);
        }

        protected override void Refresh()
        {
            Show(_panel, _model.Visible);
            if (_model.Visible)
                HudWidgets.SetText(_name, Config.Text.Get(UiStringCoverage.ActionId(_model.ActionId)));
            _shownTenths = -1;
        }

        public override void Animate(double nowMs)
        {
            if (!_model.Visible)
                return;
            HudWidgets.SetFill(_fill, _model.Progress(nowMs));
            int tenths = (int)Math.Ceiling(Math.Max(0.0, _model.EndMs - nowMs) / 100.0);
            if (tenths == _shownTenths)
                return;
            _shownTenths = tenths;
            _time.text = Config.Text.Get(HudTextIds.CastTimeLeft, new TextArg("seconds", NumberText.Tenths(tenths)));
        }
    }

    /// <summary>
    /// <c>action_toast</c> (spec 9.4 <c>ActionResult</c>, 9.9 hotbar flow): the reason text of the last refusal or parry
    /// result, from <c>ReasonCodeStrings</c>; a code without a row shows the <c>UnknownReason</c> text.
    /// </summary>
    public sealed class ToastPresenter : HudPresenter
    {
        private readonly ToastViewModel _model;
        private Label _label = null!;

        public ToastPresenter(HudConfig config, HudView view, ToastViewModel model) : base(config, view, model)
        {
            _model = model;
        }

        protected override void Build()
        {
            _label = View.Place(HudElementIds.ActionToast, HudWidgets.Label("", "hud-toast"));
            _label.style.color = Theme.Text;
            _label.style.backgroundColor = Theme.Panel;
        }

        protected override void Refresh()
        {
            Show(_label, _model.Visible);
            if (!_model.Visible)
                return;
            if (_model.TextId.Length > 0) // a level-up or rank-up (WP-M2-3): news, not a refusal
            {
                HudWidgets.SetText(_label, Config.Text.Get(_model.TextId, new TextArg(_model.ArgName, _model.ArgValue)));
                HudWidgets.Border(_label, Theme.Accent);
                return;
            }
            HudWidgets.SetText(_label, Config.Reasons.Text(_model.ReasonCode));
            HudWidgets.Border(_label, _model.IsParryResult ? Theme.Parry : Theme.Hp);
        }

        public string Text => _label.style.display == DisplayStyle.Flex ? _label.text : "";
    }

    /// <summary>
    /// Spec 9.3 <c>chat</c>, Local tab only (R-6.5-01; spec 9.12): the newest lines at the bottom ("Name: text", refusals
    /// as their <c>ReasonCodeStrings</c> text), and a text field that Enter opens and Enter sends (Escape or clicking away
    /// closes it, keeping the draft). While it is open the gameplay keys are suspended (<see cref="ChatViewModel.InputOpen"/>
    /// through the feed). Rich text is off, so a line is shown exactly as typed and cannot inject markup.
    /// TODO(M2): Party, Guild and other channel tabs; scrolling back through history.
    /// </summary>
    public sealed class ChatPresenter : HudPresenter
    {
        private readonly ChatViewModel _model;
        private readonly Action<string> _submit;
        private VisualElement _messages = null!;
        private Label _hint = null!;
        private TextField _input = null!;
        private LineElements[] _rows = Array.Empty<LineElements>();

        /// <param name="submit">Sends the draft (<see cref="HudFeedBinder.SubmitChat"/>).</param>
        public ChatPresenter(HudConfig config, HudView view, ChatViewModel model, Action<string> submit) : base(config, view, model)
        {
            _model = model;
            _submit = submit;
        }

        private sealed class LineElements
        {
            public VisualElement Root = null!;
            public Label Name = null!;
            public Label Text = null!;
        }

        protected override void Build()
        {
            var panel = View.Place(HudElementIds.Chat, HudWidgets.Panel("hud-chat", Theme));
            var tabs = HudWidgets.Box("hud-chat__tabs");
            var local = HudWidgets.Label(Config.Text.Get(HudTextIds.ChatLocal), "hud-chat__tab");
            HudWidgets.Border(local, Theme.Accent);
            tabs.Add(local);
            panel.Add(tabs);

            _messages = HudWidgets.Box("hud-chat__messages");
            _rows = new LineElements[_model.HistoryLines];
            for (int i = 0; i < _rows.Length; i++)
            {
                var row = new LineElements { Root = HudWidgets.Box("hud-chat__line") };
                row.Name = PlainLabel("hud-chat__name");
                row.Name.style.color = Theme.Accent;
                row.Text = PlainLabel("hud-chat__text");
                row.Root.Add(row.Name);
                row.Root.Add(row.Text);
                row.Root.style.display = DisplayStyle.None;
                _messages.Add(row.Root);
                _rows[i] = row;
            }
            panel.Add(_messages);

            _hint = HudWidgets.Label(Config.Text.Get(HudTextIds.ChatHint), "hud-chat__hint");
            _hint.style.color = Theme.Locked;
            panel.Add(_hint);
            _input = new TextField { name = "chat_input", maxLength = _model.MaxChars };
            _input.AddToClassList("hud-chat__input");
            _input.textSelection.selectAllOnFocus = false; // a kept draft continues where it was, not selected
            _input.textSelection.selectAllOnMouseUp = false;
            var box = _input.Q(className: TextField.inputUssClassName);
            if (box != null)
            {
                box.style.backgroundColor = Theme.Panel;
                box.style.color = Theme.Text;
                HudWidgets.Border(box, Theme.Accent);
            }
            _input.RegisterCallback<FocusOutEvent>(_ => _model.CloseInput());
            panel.Add(_input);
        }

        private static Label PlainLabel(string className)
        {
            var label = HudWidgets.Label("", className);
            label.enableRichText = false; // players' text is shown as typed, never as markup
            return label;
        }

        protected override void Refresh()
        {
            var lines = _model.Lines;
            for (int i = 0; i < _rows.Length; i++)
            {
                var row = _rows[i];
                if (i >= lines.Count)
                {
                    row.Root.style.display = DisplayStyle.None;
                    continue;
                }
                var line = lines[i];
                row.Root.style.display = DisplayStyle.Flex;
                if (line.Kind == ChatLineKind.Refusal)
                {
                    Show(row.Name, false);
                    HudWidgets.SetText(row.Text, Config.Reasons.Text(line.Text));
                    row.Text.style.color = Theme.Hp;
                }
                else
                {
                    Show(row.Name, true);
                    HudWidgets.SetText(row.Name, Config.Text.Get(HudTextIds.ChatSender, new TextArg("name", line.Name)));
                    HudWidgets.SetText(row.Text, line.Text);
                    row.Text.style.color = Theme.Text;
                }
            }

            Show(_input, _model.InputOpen);
            Show(_hint, !_model.InputOpen);
            if (_model.InputOpen && _input.focusController?.focusedElement != _input)
                _input.Focus();
        }

        /// <summary>Every frame: Enter opens the input or sends the draft, Escape closes it.</summary>
        public override void Animate(double nowMs)
        {
            var keyboard = Keyboard.current;
            if (keyboard == null)
                return;
            if (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame)
                PressEnter();
            else if (keyboard.escapeKey.wasPressedThisFrame)
                PressEscape();
        }

        /// <summary>Enter: opens the input, or sends its draft and closes it.</summary>
        public void PressEnter()
        {
            if (!_model.InputOpen)
            {
                _model.OpenInput();
                return;
            }
            string draft = _input.value;
            _input.SetValueWithoutNotify("");
            _input.Blur();
            _model.CloseInput();
            _submit(draft);
        }

        /// <summary>Escape: closes the input and keeps the draft.</summary>
        public void PressEscape()
        {
            if (!_model.InputOpen)
                return;
            _input.Blur();
            _model.CloseInput();
        }

        /// <summary>The draft in the input (tests and QA set it as if typed).</summary>
        public string Draft
        {
            get => _input.value;
            set => _input.SetValueWithoutNotify(value);
        }

        public bool InputVisible => _input.style.display == DisplayStyle.Flex;

        /// <summary>The shown lines, oldest first, as "name text" (refusals as their reason text), for tests and QA.</summary>
        public string[] ShownLines()
        {
            var shown = new System.Collections.Generic.List<string>();
            foreach (var row in _rows)
            {
                if (row.Root.style.display != DisplayStyle.Flex)
                    continue;
                shown.Add(row.Name.style.display == DisplayStyle.None ? row.Text.text : row.Name.text + " " + row.Text.text);
            }
            return shown.ToArray();
        }
    }
}
