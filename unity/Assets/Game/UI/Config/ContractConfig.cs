#nullable enable
using System.Collections.Generic;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Crafting;

namespace Game.UI.Config
{
    /// <summary>
    /// <c>UiStrings</c> ids of the contract windows, the portfolio and the forge's contract banner (spec 5.4, 9.1: no user-facing text
    /// in code; WP-M4-10 part B). An EditMode test checks that each constant has a row.
    /// </summary>
    public static class ContractTextIds
    {
        // The C window (contract_window).
        public const string Title = "contract.title";
        public const string Close = "contract.close";
        /// <summary>The close button's label for screen readers (spec 9.8).</summary>
        public const string CloseLabel = "contract.closeLabel";
        public const string TabMine = "contract.tab.mine";
        public const string TabPropose = "contract.tab.propose";
        public const string Empty = "contract.empty";
        /// <summary>{role}: {output} for {gold} gold - {status}.</summary>
        public const string Row = "contract.row";
        public const string RoleCustomer = "contract.role.customer";
        public const string RoleCrafter = "contract.role.crafter";
        /// <summary>On the board, {time} left.</summary>
        public const string StatusProposed = "contract.status.proposed";
        /// <summary>Accepted, {time} left.</summary>
        public const string StatusAccepted = "contract.status.accepted";
        /// <summary>Work started, {time} left.</summary>
        public const string StatusStarted = "contract.status.started";
        public const string StatusInProgress = "contract.status.inProgress";
        public const string StatusClosed = "contract.status.closed";
        /// <summary>{hours} h {minutes} min.</summary>
        public const string Time = "contract.time";
        /// <summary>Materials: {slots}.</summary>
        public const string Slots = "contract.slots";
        /// <summary>{item} x{count} from the customer.</summary>
        public const string SlotCustomer = "contract.slot.customer";
        /// <summary>{item} x{count} from the crafter.</summary>
        public const string SlotCrafter = "contract.slot.crafter";
        /// <summary>The crafter's slot that takes any material the input takes.</summary>
        public const string SlotAny = "contract.slot.any";
        public const string Cancel = "contract.cancel";
        /// <summary>Cancel ({gold} gold): the crafter's cancel after work started.</summary>
        public const string CancelCost = "contract.cancelCost";
        public const string CrafterPortfolio = "contract.crafterPortfolio";
        /// <summary>Contracts unlock at level {level} (M4 decision 14).</summary>
        public const string Locked = "contract.locked";

        // The propose form (the C window's Propose tab).
        /// <summary>Action: {action}.</summary>
        public const string FormAction = "contract.form.action";
        /// <summary>Make: {output}.</summary>
        public const string FormOutput = "contract.form.output";
        public const string FormPrevious = "contract.form.previous";
        public const string FormNext = "contract.form.next";
        /// <summary>{n}. {input}: {supplier}.</summary>
        public const string FormSlot = "contract.form.slot";
        public const string FormSwitch = "contract.form.switch";
        public const string FormChoose = "contract.form.choose";
        public const string FormEmpty = "contract.form.empty";
        public const string FormGold = "contract.form.gold";
        public const string FormDeadline = "contract.form.deadline";
        /// <summary>At least {gold} gold; {min} to {max} hours.</summary>
        public const string FormBounds = "contract.form.bounds";
        public const string FormPropose = "contract.form.propose";
        public const string FormNothing = "contract.form.nothing";
        public const string PickTitle = "contract.pick.title";
        public const string PickEmpty = "contract.pick.empty";
        /// <summary>{item} x{count}.</summary>
        public const string PickEntry = "contract.pick.entry";
        public const string PickCancel = "contract.pick.cancel";
        public const string ActionMold = "contract.action.mold";
        public const string ActionRefine = "contract.action.refine";
        public const string ActionConvert = "contract.action.convert";
        public const string ActionMoldMaking = "contract.action.moldMaking";
        public const string ActionAny = "contract.action.any";

        // The contract board (contract_board).
        public const string BoardTitle = "contract.board.title";
        public const string BoardEmpty = "contract.board.empty";
        /// <summary>{output} for {gold} gold - {hours} h to craft - on the board {time}.</summary>
        public const string BoardRow = "contract.board.row";
        /// <summary>Job: {job}.</summary>
        public const string BoardJob = "contract.board.job";
        public const string BoardJobAny = "contract.board.jobAny";
        /// <summary>Action: {action}.</summary>
        public const string BoardKind = "contract.board.kind";
        public const string BoardKindMold = "contract.board.kindMold";
        public const string BoardKindRecipe = "contract.board.kindRecipe";
        public const string BoardMore = "contract.board.more";
        public const string BoardFirst = "contract.board.first";
        public const string BoardAccept = "contract.board.accept";
        public const string BoardPortfolio = "contract.board.portfolio";

        // Toasts of applied answers.
        /// <summary>Contract proposed: {gold} gold held.</summary>
        public const string ToastProposed = "contract.toast.proposed";
        public const string ToastAccepted = "contract.toast.accepted";
        public const string ToastCancelled = "contract.toast.cancelled";
        /// <summary>Contract cancelled: it cost {gold} gold.</summary>
        public const string ToastCancelCost = "contract.toast.cancelCost";

        // The portfolio (the Jobs window's tab and the read-only view of another player's).
        public const string PortfolioTitle = "portfolio.title";
        public const string PortfolioBack = "portfolio.back";
        public const string PortfolioEmpty = "portfolio.empty";
        public const string PortfolioWaiting = "portfolio.waiting";
        /// <summary>{job} - {category} - {rarity} - {outcome}: {count}.</summary>
        public const string PortfolioCounter = "portfolio.counter";
        /// <summary>Showcase ({count}/{max}).</summary>
        public const string PortfolioShowcase = "portfolio.showcase";
        public const string PortfolioSlotEmpty = "portfolio.slotEmpty";
        public const string PortfolioPin = "portfolio.pin";
        public const string PortfolioUnpin = "portfolio.unpin";
        public const string PortfolioPickTitle = "portfolio.pick.title";
        public const string PortfolioPickEmpty = "portfolio.pick.empty";
        public const string PortfolioHide = "portfolio.hide";
        public const string PortfolioShow = "portfolio.show";
        public const string PortfolioHidden = "portfolio.hidden";
        public const string PortfolioAny = "portfolio.any";

        // The forge's contract banner.
        /// <summary>Contract {n}/{total}: {output} for {gold} gold.</summary>
        public const string ForgeBanner = "forge.contract.banner";
        public const string ForgeWork = "forge.contract.work";
        public const string ForgeStop = "forge.contract.stop";
        /// <summary>Crafting for contract #{id}: {output} for {gold} gold.</summary>
        public const string ForgeWorking = "forge.contract.working";
        /// <summary>From the contract: {item} x{count}.</summary>
        public const string ForgeSlot = "forge.contract.slot";

        // The journal's contracts tab and the Jobs window's portfolio tab.
        public const string JournalRuns = "quest.journal.tab.runs";
        public const string JournalContracts = "quest.journal.tab.contracts";
        public const string JobsPortfolio = "jobs.tab.portfolio";

        /// <summary>The outcome of a portfolio counter.</summary>
        public static string Outcome(PortfolioOutcome outcome) => "portfolio.outcome." + outcome switch
        {
            PortfolioOutcome.Crafted => "crafted",
            PortfolioOutcome.Refined => "refined",
            PortfolioOutcome.Converted => "converted",
            PortfolioOutcome.ConvertFailed => "convertFailed",
            PortfolioOutcome.EnchantUp => "enchantUp",
            PortfolioOutcome.EnchantSame => "enchantSame",
            PortfolioOutcome.EnchantDown => "enchantDown",
            PortfolioOutcome.ContractCompleted => "contractCompleted",
            PortfolioOutcome.ContractExpired => "contractExpired",
            PortfolioOutcome.ContractCancelled => "contractCancelled",
            _ => "none",
        };

        /// <summary>The item category of a portfolio counter (<c>PortfolioCounter.Category</c> is the category + 1; 0 none).</summary>
        public static string Category(byte category) => "portfolio.category." + category switch
        {
            1 => "sellOnly",
            2 => "material",
            3 => "equipment",
            4 => "consumable",
            5 => "moldOrTool",
            6 => "quest",
            _ => "none",
        };

        /// <summary>The action a definition stands for (a mold craft or a recipe's type).</summary>
        public static string Action(ForgeDefinition? definition) => definition == null ? ActionAny
            : definition.Kind == ForgeKind.Mold ? ActionMold
            : definition.RecipeType switch
            {
                RecipeType.Refine => ActionRefine,
                RecipeType.Convert => ActionConvert,
                _ => ActionMoldMaking,
            };

        public static readonly string[] All =
        {
            Title, Close, CloseLabel, TabMine, TabPropose, Empty, Row, RoleCustomer, RoleCrafter, StatusProposed, StatusAccepted,
            StatusStarted, StatusInProgress, StatusClosed, Time, Slots, SlotCustomer, SlotCrafter, SlotAny, Cancel, CancelCost,
            CrafterPortfolio, Locked, FormAction, FormOutput, FormPrevious, FormNext, FormSlot, FormSwitch, FormChoose, FormEmpty,
            FormGold, FormDeadline, FormBounds, FormPropose, FormNothing, PickTitle, PickEmpty, PickEntry, PickCancel, ActionMold,
            ActionRefine, ActionConvert, ActionMoldMaking, ActionAny, BoardTitle, BoardEmpty, BoardRow, BoardJob, BoardJobAny,
            BoardKind, BoardKindMold, BoardKindRecipe, BoardMore, BoardFirst, BoardAccept, BoardPortfolio, ToastProposed,
            ToastAccepted, ToastCancelled, ToastCancelCost, PortfolioTitle, PortfolioBack, PortfolioEmpty, PortfolioWaiting,
            PortfolioCounter, PortfolioShowcase, PortfolioSlotEmpty, PortfolioPin, PortfolioUnpin, PortfolioPickTitle,
            PortfolioPickEmpty, PortfolioHide, PortfolioShow, PortfolioHidden, PortfolioAny, ForgeBanner, ForgeWork, ForgeStop,
            ForgeWorking, ForgeSlot, JournalRuns, JournalContracts, JobsPortfolio,
            Outcome(PortfolioOutcome.None), Outcome(PortfolioOutcome.Crafted), Outcome(PortfolioOutcome.Refined),
            Outcome(PortfolioOutcome.Converted), Outcome(PortfolioOutcome.ConvertFailed), Outcome(PortfolioOutcome.EnchantUp),
            Outcome(PortfolioOutcome.EnchantSame), Outcome(PortfolioOutcome.EnchantDown), Outcome(PortfolioOutcome.ContractCompleted),
            Outcome(PortfolioOutcome.ContractExpired), Outcome(PortfolioOutcome.ContractCancelled),
            Category(0), Category(1), Category(2), Category(3), Category(4), Category(5), Category(6),
        };
    }

    /// <summary>
    /// The config the contract windows read (spec 5.4, 9.13; M4 decisions 10, 11, 14; WP-M4-10 part B): the bounds the form checks
    /// before sending (<c>ContractSettings</c>: the minimum gold, the deadline hours, the cancel cost; <c>EconomySettings.maxGold</c>),
    /// the forge definitions a contract names (<see cref="ForgeRules"/>), the jobs of the board's filter, the board NPCs, and the
    /// toggle key (<c>Hotkeys</c> <c>toggleContracts</c>). The server checks every request again and decides.
    /// </summary>
    public sealed class ContractConfig
    {
        private readonly HashSet<int> _boards = new HashSet<int>();
        private readonly List<JobDef> _jobs = new List<JobDef>();

        public ContractConfig(ContractSettings? settings, long maxGold, ForgeRules? forge, InventoryConfig inventory, string toggleBinding)
        {
            Settings = settings;
            MaxGold = maxGold;
            Forge = forge;
            Inventory = inventory;
            ToggleBinding = toggleBinding;
        }

        /// <summary>Null when the tables have no row: the windows then propose nothing.</summary>
        public ContractSettings? Settings { get; }

        public long MinGold => Settings?.MinGold ?? 1;

        public long MaxGold { get; }

        public int MinDeadlineHours => Settings?.MinDeadlineHours ?? 1;

        public int MaxDeadlineHours => Settings?.MaxDeadlineHours ?? 1;

        public int CancelCostPercent => Settings?.CancelCostPercent ?? 0;

        public int ShowcaseMax => Settings?.ShowcaseMax ?? 0;

        public ForgeRules? Forge { get; }

        /// <summary>Item names and rarity names (the inventory window's).</summary>
        public InventoryConfig Inventory { get; }

        /// <summary>The <c>toggleContracts</c> control path; empty when the table has no row.</summary>
        public string ToggleBinding { get; }

        /// <summary>The jobs, lowest net id first (the board's job filter).</summary>
        public IReadOnlyList<JobDef> Jobs => _jobs;

        public JobDef? Job(int netId) => _jobs.Find(j => j.NetId == netId);

        /// <summary>The materials, lowest net id first: what a crafter's slot may name (the form steps through those its input takes).</summary>
        public IReadOnlyList<ItemDef> Materials => _materials;

        private readonly List<ItemDef> _materials = new List<ItemDef>();

        /// <summary>Whether the NPC of a <c>NpcDef.netId</c> is a contract board (it offers <c>Contracts</c>).</summary>
        public bool IsBoard(int npcNetId) => _boards.Contains(npcNetId);

        public ForgeDefinition? Definition(ForgeKind kind, int defNetId) =>
            Forge != null && Forge.TryGet(kind, defNetId, out var definition) ? definition : null;

        public static ContractConfig From(ConfigSet set, InventoryConfig? inventory = null)
        {
            long maxGold = set.TryGet(typeof(EconomySettings), out var economy) && economy.Rows.Count > 0
                ? ((EconomySettings)economy.Rows[0]).MaxGold : long.MaxValue;
            var config = new ContractConfig(ContractSettings.Find(set), maxGold, ForgeRules.From(set), inventory ?? InventoryConfig.From(set),
                Hotkeys.Find(set, Hotkeys.ToggleContracts)?.Binding ?? string.Empty);
            if (set.TryGet(typeof(JobDef), out var jobs))
            {
                foreach (var row in jobs.Rows)
                    config._jobs.Add((JobDef)row);
                config._jobs.Sort((a, b) => a.NetId.CompareTo(b.NetId));
            }
            if (set.TryGet(typeof(ItemDef), out var items))
            {
                foreach (var row in items.Rows)
                {
                    if (((ItemDef)row).Category == ItemCategory.Material)
                        config._materials.Add((ItemDef)row);
                }
                config._materials.Sort((a, b) => a.NetId.CompareTo(b.NetId));
            }
            if (set.TryGet(typeof(NpcDef), out var npcs))
            {
                foreach (var row in npcs.Rows)
                {
                    if (((NpcDef)row).Offers(NpcInteraction.Contracts))
                        config._boards.Add(((NpcDef)row).NetId);
                }
            }
            return config;
        }
    }
}
