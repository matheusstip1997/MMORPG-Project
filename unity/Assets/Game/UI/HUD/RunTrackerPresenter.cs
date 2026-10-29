#nullable enable
using System.Collections.Generic;
using Game.UI.Config;
using Game.UI.ViewModels.Quests;
using Shared.Protocol;
using UnityEngine.UIElements;

namespace Game.UI.HUD
{
    /// <summary>
    /// Spec 9.3 <c>run_tracker</c> (WP-M2-6): the character's run while it is active or completed, placed by its <c>HudLayout</c>
    /// row: "Run: 1 of 2 done", the step bonus and the forfeit penalty, each step with its progress, and the claim note once every
    /// step is done or forfeited. From the server's <see cref="RunStatus"/> only; hidden without a run.
    /// </summary>
    public sealed class RunTrackerPresenter : HudPresenter
    {
        private readonly QuestConfig _quests;
        private readonly RunTrackerViewModel _model;
        private readonly List<Label> _steps = new List<Label>();
        private VisualElement _box = null!;
        private Label _title = null!;
        private Label _bonus = null!;
        private VisualElement _list = null!;
        private Label _claim = null!;

        public RunTrackerPresenter(HudConfig config, HudView view, QuestConfig quests, RunTrackerViewModel model) : base(config, view, model)
        {
            _quests = quests;
            _model = model;
        }

        public bool IsShown => _box.style.display == DisplayStyle.Flex;

        /// <summary>For tests and QA checks.</summary>
        public string TitleText => _title.text;

        public string BonusText => _bonus.text;

        public string ClaimText => _claim.text;

        public bool ClaimShown => _claim.style.display == DisplayStyle.Flex;

        public IReadOnlyList<Label> Steps => _steps;

        protected override void Build()
        {
            _box = View.Place(HudElementIds.RunTracker, HudWidgets.Panel("hud-run", Theme));
            _box.style.paddingLeft = 6;
            _box.style.paddingRight = 6;
            _title = HudWidgets.Label("", "hud-run__title");
            _title.style.color = Theme.Accent;
            _box.Add(_title);
            _bonus = HudWidgets.Label("", "hud-run__bonus");
            _bonus.style.color = Theme.Text;
            _box.Add(_bonus);
            _list = HudWidgets.Box("hud-run__steps");
            _box.Add(_list);
            _claim = HudWidgets.Label("", "hud-run__claim");
            _claim.style.color = Theme.Accent;
            _box.Add(_claim);
        }

        protected override void Refresh()
        {
            Show(_box, _model.Visible);
            if (!_model.Visible)
                return;
            var run = _model.Run!;
            HudWidgets.SetText(_title, Config.Text.Get(QuestTextIds.TrackerTitle, new TextArg("done", QuestConfig.Number(_model.DoneSteps)),
                new TextArg("total", QuestConfig.Number(run.Steps.Length))));
            HudWidgets.SetText(_bonus, Config.Text.Get(QuestTextIds.TrackerBonus, new TextArg("bonus", QuestConfig.Number(run.BonusPercent)),
                new TextArg("penalty", QuestConfig.Number(run.PenaltyPercent))));
            while (_steps.Count < run.Steps.Length)
            {
                var label = HudWidgets.Label("", "hud-run__step");
                label.style.color = Theme.Text;
                _steps.Add(label);
                _list.Add(label);
            }
            for (int i = 0; i < _steps.Count; i++)
            {
                bool used = i < run.Steps.Length;
                Show(_steps[i], used);
                if (used)
                    HudWidgets.SetText(_steps[i], _quests.Step(run.Steps[i]));
            }
            bool completed = run.State == RunState.Completed;
            Show(_claim, completed);
            HudWidgets.SetText(_claim, completed
                ? Config.Text.Get(QuestTextIds.TrackerClaim, new TextArg("exp", QuestConfig.Number(run.ClaimExp)))
                : "");
        }
    }
}
