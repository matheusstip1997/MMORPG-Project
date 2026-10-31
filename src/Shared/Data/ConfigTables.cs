using System;
using System.Collections.Generic;
using Shared.Data.Tables;

namespace Shared.Data
{
    /// <summary>Every config table the game loads. Add a row type here when you add a table file.</summary>
    public static class ConfigTables
    {
        public static readonly IReadOnlyList<Type> All = new[]
        {
            typeof(StartingValuesM1),
            typeof(NetSecurity),
            typeof(EventBusLimits),
            typeof(TelemetrySettings),
            typeof(ServerLoop),
            typeof(RegionDefaults),
            typeof(RegionDef),
            typeof(SessionSettings),
            typeof(CharacterSlots),
            typeof(CharacterCreationConfig),
            typeof(AuthApiLimits),
            typeof(ClientPrediction),
            typeof(ClientCamera),
            typeof(StatDef),
            typeof(DamageConstants),
            typeof(RankFactor),
            typeof(ElementCaps),
            typeof(ResourceRegen),
            typeof(AttributeFormula),
            typeof(ConsumptionMeterSettings),
            typeof(ActionDef),
            typeof(ActionEffectDef),
            typeof(HitShapeDef),
            typeof(ProjectileDef),
            typeof(ProjectileSettings),
            typeof(ActionBindings),
            typeof(LoginSettings),
            typeof(LoginScreens),
            typeof(LoginLayout),
            typeof(ServerDirectory),
            typeof(ReasonCodeStrings),
            typeof(UiStrings),
            typeof(EntityVisualDef),
            typeof(ClassDef),
            typeof(MonsterDef),
            typeof(SpawnPoints),
            typeof(MonsterAiSettings),
            typeof(BodyCollisionSettings),
            typeof(ItemDef),
            typeof(RarityDef),
            typeof(ContainerDef),
            // Equipment (spec 4.1 M2 item model, WP-M2-1).
            typeof(EquipSlotDef),
            typeof(ItemStatBonusDef),
            // Item model v3 (spec 4.1 M3 item model, WP-M3-2).
            typeof(MaterialTierDef),
            typeof(MaterialTagDef),
            typeof(MaterialStatDef),
            typeof(ToolDef),
            typeof(ClassGearDef),
            typeof(LootTableDef),
            typeof(LootTableEntry),
            typeof(LootSettings),
            typeof(StatusEffectDef),
            typeof(StatusModifierDef),
            typeof(BuffIconRegistry),
            // Consumable use (spec 3.4, WP-M3-3).
            typeof(ConsumableDef),
            typeof(StatusSettings),
            typeof(BuildUpDef),
            typeof(EffectBuildUpDef),
            typeof(GroundAreaSettings),
            typeof(HudSettings),
            typeof(HudLayout),
            typeof(HudThemeTokens),
            typeof(ChatSettings),
            typeof(ReliableMessageCaps),
            typeof(InventorySettings),
            typeof(Hotkeys),
            typeof(ItemIconRegistry),
            typeof(InventoryWindowSettings),
            typeof(CharacterSheetStats), // the character sheet (WP-M2-11)
            typeof(TargetLockSettings),
            // Outposts, selling, demand and vendors (spec 4.2, WP-M2-4).
            typeof(NpcDef),
            typeof(NpcVendorStock),
            typeof(PriceBase),
            typeof(RarityMult),
            typeof(PriceFloorRecovery),
            typeof(DemandCurve),
            typeof(DemandSettings),
            typeof(EconomySettings),
            // Dev tools (backlog J, A): hot reload, the strike trace and the dev views of the dev scene.
            typeof(DevTools),
            // Tools only (BotClient, backlog J): bot links, behaviours and load-test scenarios.
            typeof(BotLinkProfile),
            typeof(BotBehaviour),
            typeof(BotScenario),
            // Tools only (EconomySimulator, spec 12.5, WP-M2-12): the simulated economy runs.
            typeof(EconomySimScenario),
            // Server and tools only (admin panel v0, backlog J).
            typeof(AdminSettings),
            // Exp, level, rank and achievements (spec 3.6, WP-M2-3).
            typeof(ProgressionSettings),
            typeof(ExpCurve),
            typeof(ExpBandCurves),
            typeof(RankModifier),
            typeof(RankLevelRanges),
            typeof(AchievementDef),
            typeof(RankRequirements),
            // Skill tree and the tier-1 class lines (spec 3.5, WP-M2-5).
            typeof(SkillTreeSettings),
            typeof(SkillTree),
            typeof(SkillEvolution),
            typeof(SkillDef),
            typeof(SkillModifierDef),
            // The Bystial book (spec 14.2, WP-M2-8).
            typeof(BystialCardDef),
            typeof(BystialSlots),
            typeof(BystialFamilyBonus),
            typeof(BystialStatCap),
            typeof(BystialEffectCap),
            typeof(BystialCardArt),
            // Outpost quests and runs (spec 5.5, WP-M2-6).
            typeof(QuestCaps),
            typeof(QuestBoardSettings),
            typeof(RunBonus),
            typeof(ForfeitPenalty),
            typeof(OutpostQuestDef),
            typeof(QuestItemDrop),
            // The minimap v0 (spec 9.3, 9.12; WP-M2-10b), client-only.
            typeof(MinimapSettings),
            typeof(MinimapIconRegistry),
            typeof(MinimapMarkerDef),
            typeof(MinimapRegionTint),
            // The tutorial: unlock schedule and prompt timings (spec 12.3, WP-M2-10).
            typeof(UnlockSchedule),
            typeof(TutorialSettings),
            // Resource nodes in the world (spec 5.2, WP-M3-4).
            typeof(GatherSettings),
            typeof(NodeDef),
            typeof(GatherNodes),
            // Jobs (spec 5.1, WP-M3-1).
            typeof(JobDef),
            typeof(JobSettings),
            typeof(JobLevelCurve),
            typeof(JobXpWeights),
            // Harvesting (spec 5.2, WP-M3-5).
            typeof(NodeYieldEntry),
            typeof(HarvestSettings),
            // Job skill trees (spec 5.1 R-5.1-04, WP-M3-7).
            typeof(JobTreeSettings),
            typeof(JobBonusCap),
            typeof(JobTree),
        };
    }
}
