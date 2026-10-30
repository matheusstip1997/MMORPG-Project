#nullable enable
using Shared.Data;
using Shared.Data.Tables;
using Shared.Simulation.Jobs;

namespace Game.UI.Config
{
    /// <summary>
    /// <c>UiStrings</c> ids of the Jobs window (spec 9.1: no user-facing text in code; spec 5.1, 9.13; WP-M3-1). An EditMode
    /// test checks that each constant has a row; the job names are each <c>JobDef.nameId</c>.
    /// </summary>
    public static class JobsTextIds
    {
        public const string Title = "job.title";
        public const string Close = "job.close";
        /// <summary>The close button's label for screen readers (spec 9.8).</summary>
        public const string CloseLabel = "job.closeLabel";
        public const string SlotMain = "job.slot.main";
        public const string SlotSecondary = "job.slot.secondary";
        public const string Empty = "job.empty";
        /// <summary>Job level {level}/{max}.</summary>
        public const string Level = "job.level";
        /// <summary>{xp}/{next} XP.</summary>
        public const string Xp = "job.xp";
        public const string XpMax = "job.xpMax";
        /// <summary>Job points: {points}.</summary>
        public const string Points = "job.points";
        public const string PickTitle = "job.pickTitle";
        public const string PickHint = "job.pickHint";
        /// <summary>Requires character level {level}.</summary>
        public const string MinLevel = "job.minLevel";
        public const string PickMain = "job.pickMain";
        public const string PickSecondary = "job.pickSecondary";
        public const string ConfirmTitle = "job.confirmTitle";
        /// <summary>Pick {name} as your {slot}?</summary>
        public const string ConfirmText = "job.confirmText";
        public const string Confirm = "job.confirm";
        public const string Cancel = "job.cancel";
        public const string Waiting = "job.waiting";
        public const string ToastPicked = "job.toast.picked";
        public const string ToastLevelUp = "job.toast.levelUp";

        public static readonly string[] All =
        {
            Title, Close, CloseLabel, SlotMain, SlotSecondary, Empty, Level, Xp, XpMax, Points, PickTitle, PickHint, MinLevel, PickMain,
            PickSecondary, ConfirmTitle, ConfirmText, Confirm, Cancel, Waiting, ToastPicked, ToastLevelUp,
        };

        /// <summary>The slot's name ("Main job").</summary>
        public static string SlotName(JobSlot slot) => slot == JobSlot.Secondary ? SlotSecondary : SlotMain;
    }

    /// <summary>
    /// The config tables the Jobs window reads (spec 5.1, 9.13; WP-M3-1), from a loaded and validated <see cref="ConfigSet"/>:
    /// the job rules (the same <see cref="JobRules"/> the server checks a pick with) and the toggle key (<c>Hotkeys</c> row
    /// <c>toggleJobs</c>). The window only describes jobs; the server decides them.
    /// </summary>
    public sealed class JobsConfig
    {
        public JobsConfig(JobRules? rules, string toggleBinding)
        {
            Rules = rules;
            ToggleBinding = toggleBinding;
        }

        /// <summary>The job tables; null when they are not loaded (the window then shows no job).</summary>
        public JobRules? Rules { get; }

        /// <summary>The <c>toggleJobs</c> control path; empty when the table has no row.</summary>
        public string ToggleBinding { get; }

        /// <summary>The job of a net id; null for one the tables do not have.</summary>
        public JobDef? Job(int netId) => Rules?.FindByNetId(netId);

        public static JobsConfig From(ConfigSet set) =>
            new JobsConfig(JobRules.From(set), Hotkeys.Find(set, Hotkeys.ToggleJobs)?.Binding ?? string.Empty);
    }
}
