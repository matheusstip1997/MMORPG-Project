#nullable enable
using Shared.Data;
using Shared.Data.Tables;
using Shared.Simulation.Crafting;
using Shared.Simulation.Jobs;

namespace Game.UI.Config
{
    /// <summary>
    /// <c>UiStrings</c> ids of the gems panel at a jeweler's bench and the gem lines of the item tooltip (spec 9.1: no
    /// user-facing text in code; spec 5.3, 9.13; WP-M3-9). An EditMode test checks that each constant has a row; gem and item
    /// names are their <c>ItemDef.nameId</c>, stat names <c>stat.&lt;id&gt;</c>.
    /// </summary>
    public static class GemTextIds
    {
        public const string Tab = "gem.tab.gems";
        public const string Empty = "gem.empty";
        /// <summary>{item} (gems {used}/{slots}).</summary>
        public const string Item = "gem.item";
        /// <summary>{gem}: {stat}.</summary>
        public const string Socketed = "gem.socketed";
        /// <summary>Gem: {gem} x{count}.</summary>
        public const string ChosenGem = "gem.chosenGem";
        public const string NoGem = "gem.noGem";
        public const string Next = "gem.next";
        /// <summary>Success {percent} %.</summary>
        public const string Odds = "gem.odds";
        public const string Socket = "gem.socket";
        public const string Remove = "gem.remove";
        /// <summary>Removal costs up to {count} {item}.</summary>
        public const string Cost = "gem.cost";
        public const string Waiting = "gem.waiting";
        /// <summary>{gem} set in {item}.</summary>
        public const string ToastSocketed = "gem.toast.socketed";
        /// <summary>The {gem} shattered: {item} is unchanged.</summary>
        public const string ToastSocketFailed = "gem.toast.socketFailed";
        /// <summary>{gem} taken out of {item}.</summary>
        public const string ToastRemoved = "gem.toast.removed";
        /// <summary>The tooltip line of a socketed gem: Gem: {gem} ({stat}).</summary>
        public const string TooltipGem = "inv.tooltip.gem";
        /// <summary>The tooltip line of gear with gem slots: Gem slots {used}/{slots}.</summary>
        public const string TooltipGemSlots = "inv.tooltip.gemSlots";

        public static readonly string[] All =
        {
            Tab, Empty, Item, Socketed, ChosenGem, NoGem, Next, Odds, Socket, Remove, Cost, Waiting, ToastSocketed,
            ToastSocketFailed, ToastRemoved, TooltipGem, TooltipGemSlots,
        };
    }

    /// <summary>
    /// The config tables the gems panel reads (spec 5.3, 9.13; WP-M3-9): the gem rules (the same <see cref="GemRules"/> the
    /// server checks a socket with) and the job rules (the gem's job by net id). The panel only describes; the server checks
    /// every request and rolls the socket.
    /// </summary>
    public sealed class GemConfig
    {
        public GemConfig(GemRules? rules, JobRules? jobs)
        {
            Rules = rules;
            Jobs = jobs;
        }

        /// <summary>The gem tables; null when they are not loaded (the panel then shows nothing).</summary>
        public GemRules? Rules { get; }

        public JobRules? Jobs { get; }

        public static GemConfig From(ConfigSet set) => new GemConfig(GemRules.From(set), JobRules.From(set));

        /// <summary>The stat line of a gem ("+4 Attack" style args): the stat's <c>UiStrings</c> id and its value text.</summary>
        public static string StatNameId(GemDef gem) => UiStringCoverage.StatId(gem.StatId);
    }
}
