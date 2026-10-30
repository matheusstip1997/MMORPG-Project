#nullable enable
using System.Collections.Generic;
using System.Globalization;
using Game.UI.Config;
using Game.UI.HUD;
using Game.UI.ViewModels.Jobs;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Jobs;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.UI.Menus.Jobs
{
    /// <summary>
    /// Spec 9.13 <c>jobs_window</c> (spec 5.1; WP-M3-1): the Jobs window, placed by its <c>HudLayout</c> row. The main and the
    /// secondary slot, each with its job, job level, XP bar and job points (or "Not chosen yet"). At a job master it adds the
    /// pick section: one row per job with its level requirement and a pick button per slot, disabled with the rules' reason
    /// when the pick is not allowed; a pick first shows the confirmation step (the choice is permanent). Text from
    /// <c>UiStrings</c> and <c>ReasonCodeStrings</c>; the server decides every pick.
    /// </summary>
    public sealed class JobsWindowPresenter : HudPresenter
    {
        private const int BarHeightPx = 6;

        private readonly JobsConfig _jobs;
        private readonly JobsWindowViewModel _model;
        private readonly IJobsWindowActions _actions;
        private readonly Dictionary<JobSlot, SlotView> _slots = new Dictionary<JobSlot, SlotView>();
        private readonly List<PickRow> _rows = new List<PickRow>();
        private VisualElement _window = null!;
        private VisualElement _pick = null!;
        private Label _hint = null!;
        private VisualElement _confirm = null!;
        private Label _confirmText = null!;
        private Label _waiting = null!;
        private bool _pointerInside;

        public JobsWindowPresenter(HudConfig config, HudView view, JobsConfig jobs, JobsWindowViewModel model, IJobsWindowActions actions)
            : base(config, view, model)
        {
            _jobs = jobs;
            _model = model;
            _actions = actions;
        }

        public bool CapturesPointer => _model.Open && _pointerInside;

        public bool IsShown => _window.style.display == DisplayStyle.Flex;

        public VisualElement Window => _window;

        /// <summary>The text of a slot's line ("Main job: Blacksmith ...").</summary>
        public string SlotText(JobSlot slot) => _slots[slot].Text.text;

        public bool PickShown => _pick.style.display == DisplayStyle.Flex;

        public bool ConfirmShown => _confirm.style.display == DisplayStyle.Flex;

        public string ConfirmText => _confirmText.text;

        public int RowCount => _rows.Count;

        public Button PickButton(int row, JobSlot slot) => slot == JobSlot.Secondary ? _rows[row].Secondary : _rows[row].Main;

        /// <summary>The reason line of pick row <paramref name="row"/> (empty when every slot can be picked or none is open).</summary>
        public string RowReason(int row) => _rows[row].Reason.text;

        public Button ConfirmButton => _confirm.Q<Button>("jobs_confirm");

        public Button CancelButton => _confirm.Q<Button>("jobs_cancel");

        protected override void Build()
        {
            _window = View.Place(HudElementIds.JobsWindow, HudWidgets.Panel("jobs-window", Theme));
            _window.pickingMode = PickingMode.Position;
            _window.style.paddingLeft = 6;
            _window.style.paddingRight = 6;
            _window.style.paddingTop = 4;
            _window.style.paddingBottom = 4;
            _window.RegisterCallback<PointerEnterEvent>(_ => _pointerInside = true);
            _window.RegisterCallback<PointerLeaveEvent>(_ => _pointerInside = false);

            var header = HudWidgets.Box("jobs-header");
            header.style.flexDirection = FlexDirection.Row;
            header.style.justifyContent = Justify.SpaceBetween;
            header.pickingMode = PickingMode.Position;
            var title = HudWidgets.Label(Config.Text.Get(JobsTextIds.Title), "jobs-title");
            title.style.color = Theme.Accent;
            header.Add(title);
            var close = Button("jobs_close", Config.Text.Get(JobsTextIds.Close), () => _actions.CloseJobs());
            close.tooltip = Config.Text.Get(JobsTextIds.CloseLabel);
            header.Add(close);
            _window.Add(header);

            foreach (var slot in new[] { JobSlot.Main, JobSlot.Secondary })
            {
                var view = new SlotView(slot);
                view.Text.style.color = Theme.Text;
                view.Text.style.whiteSpace = WhiteSpace.Normal;
                view.Bar.style.height = BarHeightPx;
                view.Bar.style.backgroundColor = Theme.Panel;
                HudWidgets.Border(view.Bar, Theme.Border);
                view.Fill.style.height = Length.Percent(100);
                view.Fill.style.backgroundColor = Theme.Accent;
                view.Root.Add(view.Text);
                view.Bar.Add(view.Fill);
                view.Root.Add(view.Bar);
                view.Root.style.marginTop = 4;
                _slots[slot] = view;
                _window.Add(view.Root);
            }

            _hint = HudWidgets.Label(Config.Text.Get(JobsTextIds.PickHint), "jobs-hint");
            _hint.style.color = Theme.Text;
            _hint.style.marginTop = 6;
            _hint.style.whiteSpace = WhiteSpace.Normal;
            _window.Add(_hint);

            _pick = HudWidgets.Box("jobs-pick");
            _pick.pickingMode = PickingMode.Position;
            _pick.style.marginTop = 6;
            var caption = HudWidgets.Label(Config.Text.Get(JobsTextIds.PickTitle), "jobs-caption");
            caption.style.color = Theme.Accent;
            _pick.Add(caption);
            _window.Add(_pick);

            _confirm = HudWidgets.Box("jobs-confirm");
            _confirm.pickingMode = PickingMode.Position;
            _confirm.style.marginTop = 6;
            var confirmTitle = HudWidgets.Label(Config.Text.Get(JobsTextIds.ConfirmTitle), "jobs-caption");
            confirmTitle.style.color = Theme.Accent;
            _confirm.Add(confirmTitle);
            _confirmText = HudWidgets.Label("", "jobs-confirm__text");
            _confirmText.style.color = Theme.Text;
            _confirmText.style.whiteSpace = WhiteSpace.Normal;
            _confirm.Add(_confirmText);
            var buttons = HudWidgets.Box("jobs-confirm__buttons");
            buttons.style.flexDirection = FlexDirection.Row;
            buttons.pickingMode = PickingMode.Position;
            buttons.Add(Button("jobs_confirm", Config.Text.Get(JobsTextIds.Confirm), () => _actions.ConfirmPick()));
            buttons.Add(Button("jobs_cancel", Config.Text.Get(JobsTextIds.Cancel), () => _actions.CancelPick()));
            _confirm.Add(buttons);
            _window.Add(_confirm);

            _waiting = HudWidgets.Label(Config.Text.Get(JobsTextIds.Waiting), "jobs-waiting");
            _waiting.style.color = Theme.Text;
            _window.Add(_waiting);
        }

        protected override void Refresh()
        {
            Show(_window, _model.Open);
            if (!_model.Open)
                return;
            RefreshSlot(JobSlot.Main);
            RefreshSlot(JobSlot.Secondary);
            bool open = _model.InSlot(JobSlot.Main) == null || _model.InSlot(JobSlot.Secondary) == null;
            Show(_hint, !_model.AtJobMaster && open && _model.State != null);
            Show(_pick, _model.AtJobMaster && _model.Confirming == null);
            Show(_confirm, _model.Confirming != null);
            Show(_waiting, _model.Pending != 0);
            if (_model.AtJobMaster)
                RefreshRows();
            if (_model.Confirming is { } confirming)
            {
                string name = JobName(confirming.JobNetId);
                HudWidgets.SetText(_confirmText, Config.Text.Get(JobsTextIds.ConfirmText, new TextArg("name", name),
                    new TextArg("slot", Config.Text.Get(JobsTextIds.SlotName(confirming.Slot)))));
            }
        }

        private void RefreshSlot(JobSlot slot)
        {
            var view = _slots[slot];
            string slotName = Config.Text.Get(JobsTextIds.SlotName(slot));
            var held = _model.InSlot(slot);
            Show(view.Bar, held != null);
            if (held == null)
            {
                HudWidgets.SetText(view.Text, slotName + ": " + Config.Text.Get(JobsTextIds.Empty));
                return;
            }
            string level = Config.Text.Get(JobsTextIds.Level, new TextArg("level", Number(held.Level)), new TextArg("max", Number(_model.MaxLevel)));
            string xp = held.XpToNext == 0
                ? Config.Text.Get(JobsTextIds.XpMax)
                : Config.Text.Get(JobsTextIds.Xp, new TextArg("xp", Number(held.Xp)), new TextArg("next", Number(held.XpToNext)));
            string points = Config.Text.Get(JobsTextIds.Points, new TextArg("points", Number(held.Points)));
            HudWidgets.SetText(view.Text, slotName + ": " + JobName(held.JobNetId) + "   " + level + "   " + xp + "   " + points);
            float filled = held.XpToNext == 0 ? 1f : Mathf.Clamp01((float)held.Xp / held.XpToNext);
            view.Fill.style.width = Length.Percent(filled * 100f);
        }

        private void RefreshRows()
        {
            var jobs = _model.Jobs;
            while (_rows.Count < jobs.Count)
            {
                int index = _rows.Count;
                var row = new PickRow(index);
                row.Name.style.color = Theme.Text;
                row.Reason.style.color = Theme.Text;
                row.Reason.style.whiteSpace = WhiteSpace.Normal;
                row.Main = Button("jobs_pick_main_" + index, Config.Text.Get(JobsTextIds.PickMain), () => _actions.Pick(index, JobSlot.Main));
                row.Secondary = Button("jobs_pick_secondary_" + index, Config.Text.Get(JobsTextIds.PickSecondary),
                    () => _actions.Pick(index, JobSlot.Secondary));
                var buttons = HudWidgets.Box("jobs-row__buttons");
                buttons.style.flexDirection = FlexDirection.Row;
                buttons.pickingMode = PickingMode.Position;
                buttons.Add(row.Main);
                buttons.Add(row.Secondary);
                row.Root.Add(row.Name);
                row.Root.Add(buttons);
                row.Root.Add(row.Reason);
                row.Root.style.marginTop = 4;
                _rows.Add(row);
                _pick.Add(row.Root);
            }
            for (int i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                bool used = i < jobs.Count;
                Show(row.Root, used);
                if (!used)
                    continue;
                var job = jobs[i];
                HudWidgets.SetText(row.Name, Config.Text.Get(job.NameId) + "   "
                    + Config.Text.Get(JobsTextIds.MinLevel, new TextArg("level", Number(job.MinLevel))));
                bool main = _model.CanPick(job, JobSlot.Main);
                bool secondary = _model.CanPick(job, JobSlot.Secondary);
                row.Main.SetEnabled(main);
                row.Secondary.SetEnabled(secondary);
                HudWidgets.Border(row.Main, main ? Theme.Accent : Theme.Border);
                HudWidgets.Border(row.Secondary, secondary ? Theme.Accent : Theme.Border);
                HudWidgets.SetText(row.Reason, ReasonText(job));
            }
        }

        /// <summary>Why the next open slot cannot take <paramref name="job"/>, from <c>ReasonCodeStrings</c>; empty when it can.</summary>
        private string ReasonText(JobDef job)
        {
            var slot = _model.InSlot(JobSlot.Main) == null ? JobSlot.Main : JobSlot.Secondary;
            var check = _model.Check(job, slot);
            string? code = check switch
            {
                JobPickCheck.Ok => null,
                JobPickCheck.LevelTooLow => nameof(JobReason.JobLevelTooLow),
                JobPickCheck.MainFirst => nameof(JobReason.JobMainFirst),
                JobPickCheck.SameAsMain => nameof(JobReason.JobSameAsMain),
                JobPickCheck.NotSecondary => nameof(JobReason.JobNotSecondary),
                _ => _model.State == null ? null : nameof(JobReason.JobAlreadyChosen),
            };
            return code == null ? string.Empty : Config.Reasons.Text(code);
        }

        /// <summary>A job's display name (<c>JobDef.nameId</c>); its net id for one the tables lack.</summary>
        public string JobName(int jobNetId) => _jobs.Job(jobNetId) is { } job ? Config.Text.Get(job.NameId) : Number(jobNetId);

        private Button Button(string name, string text, System.Action onClick)
        {
            var button = new Button(onClick) { name = name, text = text };
            button.style.color = Theme.Text;
            button.style.backgroundColor = Theme.Panel;
            HudWidgets.Border(button, Theme.Border);
            return button;
        }

        private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

        private sealed class SlotView
        {
            public SlotView(JobSlot slot)
            {
                Root = HudWidgets.Box("jobs-slot");
                Root.name = "jobs_slot_" + (slot == JobSlot.Secondary ? "secondary" : "main");
                Text = HudWidgets.Label("", "jobs-slot__text");
                Bar = HudWidgets.Box("jobs-slot__bar");
                Fill = HudWidgets.Box("jobs-slot__fill");
            }

            public VisualElement Root { get; }
            public Label Text { get; }
            public VisualElement Bar { get; }
            public VisualElement Fill { get; }
        }

        private sealed class PickRow
        {
            public PickRow(int index)
            {
                Root = HudWidgets.Box("jobs-row");
                Root.name = "jobs_row_" + index;
                Root.pickingMode = PickingMode.Position;
                Name = HudWidgets.Label("", "jobs-row__name");
                Reason = HudWidgets.Label("", "jobs-row__reason");
            }

            public VisualElement Root { get; }
            public Label Name { get; }
            public Label Reason { get; }
            public Button Main { get; set; } = null!;
            public Button Secondary { get; set; } = null!;
        }
    }
}
