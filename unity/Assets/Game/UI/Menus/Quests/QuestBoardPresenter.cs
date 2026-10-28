#nullable enable
using System.Collections.Generic;
using Game.UI.Config;
using Game.UI.HUD;
using Game.UI.Menus.Outpost;
using Game.UI.ViewModels.Quests;
using Shared.Protocol;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.UI.Menus.Quests
{
    /// <summary>
    /// Spec 9.13 (M2) <c>quest_board</c> (WP-M2-6): the outpost's quest board and run builder v1, placed by its <c>HudLayout</c> row.
    /// Without a run: the offers ("Kill 7 Demon: 140 exp"), each a toggle that picks it for the run (its pick number shown), the
    /// picks line with the preview exp after the step bonus, and accept and clear. With a run: its steps with their progress, a
    /// hand-in button for an active deliver or return step and a forfeit button for every active step, and the claim button once
    /// the run is completed. Text from <c>UiStrings</c>; redraws on the view model's <c>Changed</c> only; the server decides every
    /// reward, step and claim.
    /// </summary>
    public sealed class QuestBoardPresenter : HudPresenter
    {
        private readonly QuestConfig _quests;
        private readonly QuestBoardViewModel _model;
        private readonly IQuestBoardActions _actions;
        private readonly List<Button> _offers = new List<Button>();
        private readonly List<VisualElement> _steps = new List<VisualElement>();
        private VisualElement _window = null!;
        private VisualElement _boardSection = null!;
        private VisualElement _offerList = null!;
        private Label _empty = null!;
        private Label _picked = null!;
        private Label _refresh = null!;
        private Button _accept = null!;
        private VisualElement _runSection = null!;
        private VisualElement _stepList = null!;
        private Button _claim = null!;
        private Label _title = null!;
        private Label _journalNote = null!;
        private bool _pointerInside;

        public QuestBoardPresenter(HudConfig config, HudView view, QuestConfig quests, QuestBoardViewModel model, IQuestBoardActions actions)
            : base(config, view, model)
        {
            _quests = quests;
            _model = model;
            _actions = actions;
        }

        public bool CapturesPointer => _model.Open && _pointerInside;

        public bool IsShown => _window.style.display == DisplayStyle.Flex;

        public VisualElement Window => _window;

        /// <summary>For tests and QA checks.</summary>
        public int OfferCount => _offers.FindAll(o => o.style.display == DisplayStyle.Flex).Count;

        public Button Offer(int index) => _offers[index];

        public string PickedText => _picked.text;

        public bool AcceptEnabled => _accept.enabledSelf;

        public bool BoardShown => _boardSection.style.display == DisplayStyle.Flex;

        public bool RunShown => _runSection.style.display == DisplayStyle.Flex;

        public int StepCount => _steps.FindAll(s => s.style.display == DisplayStyle.Flex).Count;

        public VisualElement Step(int index) => _steps[index];

        public Button ClaimButton => _claim;

        protected override void Build()
        {
            _window = View.Place(HudElementIds.QuestBoard, HudWidgets.Panel("quest-board", Theme));
            _window.pickingMode = PickingMode.Position;
            _window.style.paddingLeft = 6;
            _window.style.paddingRight = 6;
            _window.style.paddingTop = 4;
            _window.RegisterCallback<PointerEnterEvent>(_ => _pointerInside = true);
            _window.RegisterCallback<PointerLeaveEvent>(_ => _pointerInside = false);
            var header = OutpostWidgets.Header(Config, Theme, QuestTextIds.Title, "quest_close", () => _actions.CloseBoard());
            _title = (Label)header[0];
            _window.Add(header);
            // WP-M2-11: the journal's note (the run view away from an outpost sends no run action).
            _journalNote = HudWidgets.Label("", "quest-board__journal");
            _journalNote.style.color = Theme.Text;
            _journalNote.style.whiteSpace = WhiteSpace.Normal;
            _window.Add(_journalNote);

            _boardSection = HudWidgets.Box("quest-board__offers");
            _boardSection.pickingMode = PickingMode.Position;
            _offerList = HudWidgets.Box("quest-board__list");
            _offerList.pickingMode = PickingMode.Position;
            _boardSection.Add(_offerList);
            _empty = HudWidgets.Label(Config.Text.Get(QuestTextIds.Empty), "quest-board__empty");
            _empty.style.color = Theme.Text;
            _boardSection.Add(_empty);
            _picked = HudWidgets.Label("", "quest-board__picked");
            _picked.style.color = Theme.Accent;
            _boardSection.Add(_picked);
            var buttons = HudWidgets.Box("quest-board__buttons");
            buttons.style.flexDirection = FlexDirection.Row;
            buttons.pickingMode = PickingMode.Position;
            _accept = OutpostWidgets.Button(Theme, "quest_accept", Config.Text.Get(QuestTextIds.Accept), () => _actions.AcceptRun());
            buttons.Add(_accept);
            buttons.Add(OutpostWidgets.Button(Theme, "quest_clear", Config.Text.Get(QuestTextIds.ClearPicks), () => _actions.ClearPicks()));
            _boardSection.Add(buttons);
            _refresh = HudWidgets.Label("", "quest-board__refresh");
            _refresh.style.color = Theme.Text;
            _boardSection.Add(_refresh);
            _window.Add(_boardSection);

            _runSection = HudWidgets.Box("quest-board__run");
            _runSection.pickingMode = PickingMode.Position;
            var runTitle = HudWidgets.Label(Config.Text.Get(QuestTextIds.RunTitle), "quest-board__run-title");
            runTitle.style.color = Theme.Accent;
            _runSection.Add(runTitle);
            _stepList = HudWidgets.Box("quest-board__steps");
            _stepList.pickingMode = PickingMode.Position;
            _runSection.Add(_stepList);
            _claim = OutpostWidgets.Button(Theme, "quest_claim", "", () => _actions.Claim());
            _runSection.Add(_claim);
            _window.Add(_runSection);
        }

        protected override void Refresh()
        {
            Show(_window, _model.Open);
            if (!_model.Open)
                return;
            bool hasRun = _model.HasRun;
            bool journal = _model.Journal;
            HudWidgets.SetText(_title, Config.Text.Get(journal ? QuestTextIds.JournalTitle : QuestTextIds.Title));
            Show(_journalNote, journal);
            HudWidgets.SetText(_journalNote, !journal ? "" : Config.Text.Get(hasRun ? QuestTextIds.JournalNote : QuestTextIds.JournalEmpty));
            Show(_boardSection, !hasRun && !journal);
            Show(_runSection, hasRun);
            if (hasRun)
                RefreshRun(_model.Run!);
            else if (!journal)
                RefreshBoard();
        }

        /// <summary>The journal's note while it shows (WP-M2-11); empty otherwise.</summary>
        public string JournalText => _journalNote.style.display == DisplayStyle.Flex ? _journalNote.text : "";

        public string TitleText => _title.text;

        private void RefreshBoard()
        {
            var offers = _model.Board?.Offers ?? System.Array.Empty<QuestOffer>();
            while (_offers.Count < offers.Length)
            {
                int index = _offers.Count;
                var row = OutpostWidgets.Button(Theme, "quest_offer_" + index, "", () => OnOffer(index));
                row.style.unityTextAlign = TextAnchor.MiddleLeft;
                _offers.Add(row);
                _offerList.Add(row);
            }
            for (int i = 0; i < _offers.Count; i++)
            {
                bool used = i < offers.Length;
                Show(_offers[i], used);
                if (!used)
                    continue;
                var offer = offers[i];
                int pick = IndexOf(_model.Picked, offer.Slot);
                string task = _quests.Task(offer.Kind, offer.MonsterNetId, offer.ItemNetId, offer.Count);
                string line = Config.Text.Get(QuestTextIds.Offer, new TextArg("task", task), new TextArg("exp", QuestConfig.Number(offer.RewardExp)));
                _offers[i].text = pick < 0 ? line : (pick + 1) + ". " + line;
                HudWidgets.Border(_offers[i], pick >= 0 ? Theme.Accent : Theme.Border);
            }
            Show(_empty, _model.Board != null && offers.Length == 0);
            int picked = _model.Picked.Count;
            HudWidgets.SetText(_picked, picked == 0 ? "" : Config.Text.Get(QuestTextIds.Picked,
                new TextArg("count", QuestConfig.Number(picked)), new TextArg("max", QuestConfig.Number(_model.MaxSteps)),
                new TextArg("exp", QuestConfig.Number(_quests.PreviewExp(_model.PickedRewards(), picked)))));
            _accept.SetEnabled(_model.Confirm() != null);
            HudWidgets.SetText(_refresh, _model.Board == null ? "" : Config.Text.Get(QuestTextIds.Refresh,
                new TextArg("minutes", QuestConfig.Number((_model.Board.RefreshInMs + 59_999) / 60_000))));
        }

        private void RefreshRun(RunStatus run)
        {
            while (_steps.Count < run.Steps.Length)
            {
                byte index = (byte)_steps.Count;
                var row = HudWidgets.Box("quest-board__step");
                row.style.flexDirection = FlexDirection.Row;
                row.style.justifyContent = Justify.SpaceBetween;
                row.pickingMode = PickingMode.Position;
                var label = HudWidgets.Label("", "quest-board__step-text");
                label.style.color = Theme.Text;
                label.style.flexShrink = 1;
                row.Add(label);
                row.Add(OutpostWidgets.Button(Theme, "quest_handin_" + index, Config.Text.Get(QuestTextIds.HandIn), () => _actions.HandIn(index)));
                row.Add(OutpostWidgets.Button(Theme, "quest_forfeit_" + index, Config.Text.Get(QuestTextIds.Forfeit), () => _actions.Forfeit(index)));
                _steps.Add(row);
                _stepList.Add(row);
            }
            for (int i = 0; i < _steps.Count; i++)
            {
                bool used = i < run.Steps.Length;
                Show(_steps[i], used);
                if (!used)
                    continue;
                var step = run.Steps[i];
                bool active = step.State == RunStepState.Active && !_model.Journal; // the journal sends no run action
                HudWidgets.SetText((Label)_steps[i][0], _quests.Step(step));
                Show(_steps[i][1], active && step.Kind != Shared.Data.Tables.QuestKind.Kill);
                Show(_steps[i][2], active);
            }
            bool completed = run.State == RunState.Completed;
            Show(_claim, completed && !_model.Journal);
            _claim.text = Config.Text.Get(QuestTextIds.Claim, new TextArg("exp", QuestConfig.Number(run.ClaimExp)));
        }

        private void OnOffer(int index)
        {
            var offers = _model.Board?.Offers;
            if (offers != null && index < offers.Length)
                _actions.ToggleOffer(offers[index].Slot);
        }

        private static int IndexOf(IReadOnlyList<byte> picked, byte slot)
        {
            for (int i = 0; i < picked.Count; i++)
            {
                if (picked[i] == slot)
                    return i;
            }
            return -1;
        }
    }
}
