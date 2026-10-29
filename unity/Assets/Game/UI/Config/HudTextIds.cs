namespace Game.UI.Config
{
    /// <summary>
    /// <c>UiStrings</c> ids the HUD uses (spec 9.1: no user-facing text in code; backlog I). An EditMode test checks that
    /// each constant has a row. Names of actions, monsters, ranks and statuses use the prefixes of
    /// <c>Shared.Data.Tables.UiStringCoverage</c>, which the DataValidator checks.
    /// </summary>
    public static class HudTextIds
    {
        public const string LabelHp = "hud.label.hp";
        public const string LabelMp = "hud.label.mp";
        public const string LabelStamina = "hud.label.st";
        public const string ResourceValue = "hud.value.resource";
        public const string LabelToxicity = "hud.label.tox";
        public const string LabelSatiety = "hud.label.sat";
        public const string StatsTitle = "hud.stats.title";
        public const string StatsCombat = "hud.stats.combat";
        public const string StatsAttributes = "hud.stats.attributes";
        public const string Parry = "hud.parry";
        public const string Dodge = "hud.dodge";
        public const string KeyParry = "hud.key.parry";
        public const string KeyDodge = "hud.key.dodge";
        public const string CooldownSeconds = "hud.cooldown.seconds";
        public const string CastTimeLeft = "hud.cast.timeLeft";
        public const string GaugeLocked = "hud.gauge.locked";
        public const string ChatLocal = "hud.chat.local";
        public const string ChatHint = "hud.chat.hint";
        public const string ChatSender = "hud.chat.sender";
        public const string FlagArmed = "hud.flag.armed";
        public const string FlagMounted = "hud.flag.mounted";
        public const string FlagPet = "hud.flag.pet";
        public const string PlateRedName = "hud.plate.redName";
        public const string PlateLocked = "hud.plate.locked";

        /// <summary>The stagger marker on a monster's name plate while its poise is broken (R-3.3-01).</summary>
        public const string PlateStaggered = "hud.plate.staggered";
        public const string HitDodged = "hud.hit.dodged";
        public const string HitParried = "hud.hit.parried";
        public const string HitHeal = "hud.hit.heal";
        public const string BossTitle = "hud.boss.title";
        public const string Stacks = "hud.stacks";

        /// <summary>The pickup prompt at a ground drop in range (backlog G); <c>{key}</c> is the pickup binding's key name.</summary>
        public const string PickupPrompt = "hud.pickup.prompt";

        /// <summary>Ends a hotbar slot label cut to fit its slot (QA wave 6 F5).</summary>
        public const string SlotLabelEllipsis = "hud.slot.ellipsis";

        /// <summary>Level and rank in the resource panel (spec 9.3, WP-M2-3): <c>{level}</c>, <c>{rank}</c> (the <c>rank.*</c> text).</summary>
        public const string ProgressLine = "hud.progress.line";

        /// <summary>The exp note above the exp bar while the band modifier is below 1 (R-3.6-04): <c>{multiplier}</c>.</summary>
        public const string ExpNote = "hud.exp.note";

        /// <summary>The line above the exp bar (WP-M2-11): <c>{level}</c>, <c>{rank}</c>, <c>{percent}</c> of the level's exp.</summary>
        public const string ExpLabel = "hud.exp.label";

        /// <summary>The exp bar's hover text (WP-M2-11): <c>{exp}</c> of <c>{need}</c>.</summary>
        public const string ExpTooltip = "hud.exp.tooltip";

        /// <summary>The exp bar's hover text at the level cap (WP-M2-11), where no exp is needed.</summary>
        public const string ExpTooltipMax = "hud.exp.tooltipMax";

        /// <summary>A buff row chip's hover text (WP-M2-11): <c>{name}</c>, <c>{seconds}</c> left.</summary>
        public const string BuffTooltip = "hud.buff.tooltip";

        /// <summary>The same with stacks: <c>{name}</c>, <c>{stacks}</c>, <c>{seconds}</c>.</summary>
        public const string BuffTooltipStacks = "hud.buff.tooltipStacks";

        /// <summary>The shortcut row's arrow (spec 9.3): expanded <c>&gt;</c> hides the buttons.</summary>
        public const string ShortcutCollapse = "hud.shortcut.collapse";

        /// <summary>The collapsed arrow <c>&lt;</c> shows them again.</summary>
        public const string ShortcutExpand = "hud.shortcut.expand";

        /// <summary>The arrow's accessible name (spec 9.8: every icon-only control has a text label).</summary>
        public const string ShortcutArrowName = "hud.shortcut.arrow.name";

        /// <summary>A shortcut button's label ("INV", spec 9.3): <c>hud.shortcut.&lt;button&gt;</c>.</summary>
        public static string ShortcutLabel(string buttonId) => "hud.shortcut." + buttonId;

        /// <summary>A shortcut button's hover name ("Inventory ({key})"): <c>hud.shortcut.&lt;button&gt;.name</c>.</summary>
        public static string ShortcutName(string buttonId) => "hud.shortcut." + buttonId + ".name";

        /// <summary>The level-up toast (WP-M2-3): <c>{level}</c>.</summary>
        public const string ToastLevelUp = "hud.toast.levelUp";

        /// <summary>The rank-up toast (R-3.6-04): <c>{rank}</c>.</summary>
        public const string ToastRankUp = "hud.toast.rankUp";

        /// <summary>The tutorial prompt's dismiss hint (spec 12.3, WP-M2-10): <c>{key}</c> is the <c>dismissPrompt</c> key.</summary>
        public const string TutorialDismiss = "tutorial.dismiss";

        /// <summary>The tutorial prompt's close button.</summary>
        public const string TutorialClose = "tutorial.close";

        /// <summary>The key label of an <c>ActionBindings</c> skill slot (spec 9.6): <c>hud.key.slot&lt;n&gt;</c>.</summary>
        public static string KeySlot(int skillSlot) => "hud.key.slot" + skillSlot;

        /// <summary>
        /// Optional short name of an action for its hotbar slot when the full name (<c>action.&lt;id&gt;</c>) does not fit at
        /// the slot label size (QA wave 6 F5): <c>action.&lt;id&gt;.short</c>.
        /// </summary>
        public static string ActionShort(string actionId) => Shared.Data.Tables.UiStringCoverage.ActionId(actionId) + ".short";
    }
}
