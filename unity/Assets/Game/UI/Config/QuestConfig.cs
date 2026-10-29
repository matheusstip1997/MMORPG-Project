#nullable enable
using System.Globalization;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Quests;

namespace Game.UI.Config
{
    /// <summary>
    /// <c>UiStrings</c> ids of the quest board, the run builder and the run tracker (spec 9.1: no user-facing text in code; spec
    /// 9.3 <c>run_tracker</c>, 9.13 M2; WP-M2-6). An EditMode test checks that each constant has a row.
    /// </summary>
    public static class QuestTextIds
    {
        public const string Title = "quest.title";
        /// <summary>The outpost sale window's button that opens the board.</summary>
        public const string Open = "quest.open";
        public const string Empty = "quest.empty";
        public const string Refresh = "quest.refresh";
        public const string Kill = "quest.task.kill";
        public const string Deliver = "quest.task.deliver";
        public const string ReturnItem = "quest.task.returnItem";
        public const string Offer = "quest.offer";
        public const string Picked = "quest.picked";
        public const string Accept = "quest.accept";
        public const string ClearPicks = "quest.clear";
        public const string RunTitle = "quest.run.title";
        public const string StepActive = "quest.step.active";
        public const string StepDone = "quest.step.done";
        public const string StepForfeited = "quest.step.forfeited";
        public const string HandIn = "quest.handIn";
        public const string Forfeit = "quest.forfeit";
        public const string Claim = "quest.claim";
        public const string TrackerTitle = "hud.run.title";
        public const string TrackerBonus = "hud.run.bonus";
        public const string TrackerClaim = "hud.run.claim";
        /// <summary>The toast of a committed claim (news, not a refusal).</summary>
        public const string ToastClaimed = "hud.toast.runClaimed";
        /// <summary>The journal (WP-M2-11): the board's run view away from an outpost; its title, its note, no run.</summary>
        public const string JournalTitle = "quest.journal.title";
        public const string JournalNote = "quest.journal.note";
        public const string JournalEmpty = "quest.journal.empty";

        public static readonly string[] All =
        {
            Title, Open, Empty, Refresh, Kill, Deliver, ReturnItem, Offer, Picked, Accept, ClearPicks, RunTitle, StepActive, StepDone,
            StepForfeited, HandIn, Forfeit, Claim, TrackerTitle, TrackerBonus, TrackerClaim, ToastClaimed, JournalTitle, JournalNote,
            JournalEmpty,
        };
    }

    /// <summary>
    /// What the quest board and the run tracker read from the config tables (spec 5.5, 9.13; WP-M2-6): the run caps, the step bonus
    /// and the forfeit penalty (the builder previews a run's exp with the shared <see cref="RunMath"/>; the server locks the real
    /// numbers), and the names of monsters and items for the task lines.
    /// </summary>
    public sealed class QuestConfig
    {
        public QuestConfig(int maxStepsPerRun, RunBonus bonus, ForfeitPenalty penalty, HudConfig hud, InventoryConfig inventory)
        {
            MaxStepsPerRun = maxStepsPerRun;
            Bonus = bonus;
            Penalty = penalty;
            Hud = hud;
            Inventory = inventory;
        }

        public int MaxStepsPerRun { get; }
        public RunBonus Bonus { get; }
        public ForfeitPenalty Penalty { get; }
        public HudConfig Hud { get; }
        public InventoryConfig Inventory { get; }

        /// <summary>The <c>Hotkeys</c> row <c>toggleJournal</c> (WP-M2-11); empty without the row.</summary>
        public string JournalBinding { get; private set; } = "";

        /// <summary>What a run of these completed steps would pay with the step bonus (the builder's preview).</summary>
        public long PreviewExp(long rewards, int steps) =>
            RunMath.ClaimExp(rewards, RunMath.BonusPercent(steps, Bonus.PerStepPercent, Bonus.CapPercent), 0);

        /// <summary>"Kill 5 Demon", "Deliver 3 Demon Horn (Demon)", "Return 2 Demon Sigil (Demon)".</summary>
        public string Task(QuestKind kind, ushort monsterNetId, ushort itemNetId, int count)
        {
            var text = Hud.Text;
            string monster = Hud.Monster(monsterNetId) is { } def ? text.Get("monster." + def.Id) : "#" + monsterNetId;
            string item = Inventory.Item(itemNetId) is { } info ? text.Get(info.Def.NameId) : "#" + itemNetId;
            string id = kind == QuestKind.Kill ? QuestTextIds.Kill : kind == QuestKind.Deliver ? QuestTextIds.Deliver : QuestTextIds.ReturnItem;
            return text.Get(id, new TextArg("count", Number(count)), new TextArg("monster", monster), new TextArg("item", item));
        }

        /// <summary>A step line of the run: its task with progress, done, or forfeited.</summary>
        public string Step(RunStepEntry step)
        {
            string task = Task(step.Kind, step.MonsterNetId, step.ItemNetId, step.Required);
            string id = step.State == RunStepState.Done ? QuestTextIds.StepDone
                : step.State == RunStepState.Forfeited ? QuestTextIds.StepForfeited : QuestTextIds.StepActive;
            return Hud.Text.Get(id, new TextArg("task", task), new TextArg("progress", Number(step.Progress)),
                new TextArg("required", Number(step.Required)));
        }

        public static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

        public static QuestConfig From(ConfigSet set, HudConfig hud, InventoryConfig inventory)
        {
            int maxSteps = set.TryGet(typeof(QuestCaps), out var caps) && caps.Rows.Count == 1 ? ((QuestCaps)caps.Rows[0]).MaxStepsPerRun : 1;
            var bonus = set.TryGet(typeof(RunBonus), out var bonusTable) && bonusTable.Rows.Count == 1 ? (RunBonus)bonusTable.Rows[0] : new RunBonus();
            var penalty = set.TryGet(typeof(ForfeitPenalty), out var penaltyTable) && penaltyTable.Rows.Count == 1
                ? (ForfeitPenalty)penaltyTable.Rows[0]
                : new ForfeitPenalty();
            return new QuestConfig(maxSteps, bonus, penalty, hud, inventory)
            {
                JournalBinding = Hotkeys.Find(set, Hotkeys.ToggleJournal)?.Binding ?? "",
            };
        }
    }
}
