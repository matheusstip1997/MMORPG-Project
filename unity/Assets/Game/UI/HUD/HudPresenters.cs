#nullable enable
using System;
using Game.UI.Config;
using Game.UI.ViewModels.Hud;
using Shared.Data.Tables;
using Shared.Protocol;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace Game.UI.HUD
{
    /// <summary>The spec 9.3 element ids HUD v1 places (backlog I); each has a <c>HudLayout</c> row.</summary>
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

        public static readonly string[] All =
        {
            ResourcePanel, BossBar, Chat, ActionToast, CastBar, BuffRow, ParryDodge, HotbarLeft, Gauge, HotbarRight,
        };
    }

    /// <summary>
    /// Spec 9.3 <c>resource_panel</c>: HP, MP and stamina bars with "current / max". TODO(M2): the TOX and SAT bars, from
    /// <c>StatsChanged</c> (spec 9.4); M1 has no toxicity or satiety.
    /// </summary>
    public sealed class ResourcePanelPresenter : HudPresenter
    {
        private readonly ResourcePanelViewModel _model;
        private readonly VisualElement[] _fills = new VisualElement[3];
        private readonly Label[] _values = new Label[3];

        public ResourcePanelPresenter(HudConfig config, HudView view, ResourcePanelViewModel model) : base(config, view, model)
        {
            _model = model;
        }

        protected override void Build()
        {
            var panel = View.Place(HudElementIds.ResourcePanel, HudWidgets.Panel("hud-resources", Theme));
            AddRow(panel, 0, HudTextIds.LabelHp, Theme.Hp);
            AddRow(panel, 1, HudTextIds.LabelMp, Theme.Mp);
            AddRow(panel, 2, HudTextIds.LabelStamina, Theme.Stamina);
        }

        private void AddRow(VisualElement panel, int index, string labelId, Color color)
        {
            var row = HudWidgets.Box("hud-resources__row");
            row.Add(HudWidgets.Label(Config.Text.Get(labelId), "hud-resources__label"));
            var bar = HudWidgets.Bar("hud-resources__bar", color, out _fills[index]);
            _values[index] = HudWidgets.Label("", "hud-resources__value");
            bar.Add(_values[index]);
            row.Add(bar);
            panel.Add(row);
        }

        protected override void Refresh()
        {
            SetRow(0, _model.Hp, _model.MaxHp);
            SetRow(1, _model.Mp, _model.MaxMp);
            SetRow(2, _model.Stamina, _model.MaxStamina);
        }

        private void SetRow(int index, int value, int max)
        {
            HudWidgets.SetFill(_fills[index], _model.HasValues ? ResourcePanelViewModel.Fraction(value, max) : 0f);
            HudWidgets.SetText(_values[index], _model.HasValues
                ? Config.Text.Get(HudTextIds.ResourceValue, new TextArg("current", NumberText.Of(value)), new TextArg("max", NumberText.Of(max)))
                : "");
        }

        /// <summary>The value text of a row (0 HP, 1 MP, 2 stamina), for tests and QA checks.</summary>
        public string ValueText(int index) => _values[index].text;
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
        }

        /// <summary>
        /// The font size that fits a slot label's longest word on one line (QA wave 5 V2: "Flamethrower" broke mid-word):
        /// the table's <paramref name="baseSize"/>, smaller in proportion when that word, measured at
        /// <paramref name="measuredAtSize"/>, is wider than <paramref name="availableWidth"/>. Never larger than the base.
        /// </summary>
        public static float FitLabelSize(float baseSize, float measuredAtSize, float longestWordWidth, float availableWidth)
        {
            if (longestWordWidth <= 0f || measuredAtSize <= 0f || availableWidth <= 0f)
                return baseSize;
            float widthAtBase = longestWordWidth * baseSize / measuredAtSize;
            return widthAtBase <= availableWidth ? baseSize : (float)Math.Floor(baseSize * availableWidth / widthAtBase * 10f) / 10f;
        }

        private void FitName(SlotElements slot)
        {
            float baseSize = Theme.Tokens.FontSizeSlotLabel;
            string text = slot.Name.text;
            float available = slot.Root.contentRect.width;
            float current = slot.Name.resolvedStyle.fontSize;
            if (text.Length == 0 || float.IsNaN(available) || available <= 0f || float.IsNaN(current) || current <= 0f)
            {
                slot.Name.style.fontSize = baseSize;
                return;
            }
            float widest = 0f;
            foreach (var word in text.Split(' '))
            {
                if (word.Length > 0)
                    widest = Math.Max(widest, slot.Name.MeasureTextSize(word, 0, VisualElement.MeasureMode.Undefined, 0,
                        VisualElement.MeasureMode.Undefined).x);
            }
            slot.Name.style.fontSize = FitLabelSize(baseSize, current, widest, available);
        }

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
                HudWidgets.SetText(slot.Name, empty ? "" : Config.Text.Get(UiStringCoverage.ActionId(model!.ActionId)));
                FitName(slot);
                slot.Root.EnableInClassList("hud-slot--empty", empty);
            }
        }

        public override void Animate(double nowMs)
        {
            foreach (var slot in _slots)
            {
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

        public ParryDodgePresenter(HudConfig config, HudView view, ChargesViewModel model) : base(config, view, model)
        {
            _model = model;
        }

        protected override void Build()
        {
            var column = View.Place(HudElementIds.ParryDodge, HudWidgets.Box("hud-charges"));
            column.Add(Square("parry", HudTextIds.Parry, HudTextIds.KeyParry, Theme.Parry, out _parryCount, out _parryFill));
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
        }

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
