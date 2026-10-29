#nullable enable
using System;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Simulation.Skills;

namespace Game.UI.Config
{
    /// <summary>
    /// <c>UiStrings</c> ids of the skill tree window (spec 9.1: no user-facing text in code; spec 3.5, WP-M2-5). An EditMode
    /// test checks that each constant has a row. Class and node names use the prefixes of
    /// <c>Shared.Data.Tables.UiStringCoverage</c>, which the DataValidator checks.
    /// </summary>
    public static class SkillTreeTextIds
    {
        public const string Title = "skill.title";
        public const string Close = "skill.close";
        /// <summary>The close button's label for screen readers (spec 9.8: icon-only controls have a text label).</summary>
        public const string CloseLabel = "skill.closeLabel";
        public const string Points = "skill.points";
        public const string Level = "skill.level";
        public const string NodeLevel = "skill.nodeLevel";
        public const string MinLevel = "skill.minLevel";
        public const string Requires = "skill.requires";
        public const string EvolvesFrom = "skill.evolvesFrom";
        public const string Weapons = "skill.weapons";
        public const string Unlocks = "skill.unlocks";
        public const string OnSlot = "skill.onSlot";
        public const string DragHint = "skill.dragHint";
        public const string EmptyTree = "skill.emptyTree";
        public const string Unavailable = "skill.unavailable";
        public const string Learn = "skill.learn";
        public const string Upgrade = "skill.upgrade";
        public const string Evolve = "skill.evolve";
        public const string Unbar = "skill.unbar";
        public const string Respec = "skill.respec";
        public const string AdvanceTitle = "skill.advanceTitle";
        public const string Advance = "skill.advance";
        public const string ToastLearned = "skill.toast.learned";
        public const string ToastAdvanced = "skill.toast.advanced";
        public const string ToastRespecced = "skill.toast.respecced";

        public static readonly string[] All =
        {
            Title, Close, CloseLabel, Points, Level, NodeLevel, MinLevel, Requires, EvolvesFrom, Weapons, Unlocks, OnSlot, DragHint,
            EmptyTree, Unavailable, Learn, Upgrade, Evolve, Unbar, Respec, AdvanceTitle, Advance, ToastLearned, ToastAdvanced,
            ToastRespecced,
        };

        /// <summary>A node kind's name (<c>skill.kind.active</c> ...).</summary>
        public static string Kind(SkillNodeKind kind) => "skill.kind." + ConfigTableReader.CamelCase(kind.ToString());

        /// <summary>A weapon type's name (<c>skill.weapon.bigSword</c> ...).</summary>
        public static string Weapon(WeaponType type) => "skill.weapon." + ConfigTableReader.CamelCase(type.ToString());
    }

    /// <summary>
    /// The config tables the skill tree window reads (spec 3.5, 9.13 pattern; WP-M2-5): the tree rules (the same
    /// <see cref="SkillRules"/> the server and prediction use) and the toggle key (<c>Hotkeys</c> <c>toggleSkillTree</c>).
    /// </summary>
    public sealed class SkillTreeConfig
    {
        public SkillTreeConfig(SkillRules? rules, string toggleBinding)
        {
            Rules = rules;
            ToggleBinding = toggleBinding;
        }

        /// <summary>Null when the tables have no skill tree (the window then shows nothing).</summary>
        public SkillRules? Rules { get; }

        /// <summary>Input System path of the <c>Hotkeys</c> row <c>toggleSkillTree</c>.</summary>
        public string ToggleBinding { get; }

        public static SkillTreeConfig From(ConfigSet set) =>
            new SkillTreeConfig(SkillRules.From(set), Hotkeys.Find(set, Hotkeys.ToggleSkillTree)?.Binding ?? "");

        /// <summary>The <c>UiStrings</c> ids of the weapon names a node lists, in its order.</summary>
        public static string[] WeaponTextIds(WeaponType[] weapons) => Array.ConvertAll(weapons, SkillTreeTextIds.Weapon);
    }
}
