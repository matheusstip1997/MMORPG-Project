using System;
using System.Collections.Generic;
using Shared.Data.Tables;

namespace Shared.Data
{
    /// <summary>What a changed table means to a running GameServer's dev hot reload (spec 11.7).</summary>
    public enum ReloadClass
    {
        /// <summary>Tuning tables: swapped into the running world at a tick boundary.</summary>
        Hot,
        /// <summary>The server sized buffers or started services from it: a change rejects the reload ("needs a restart").</summary>
        Restart,
        /// <summary>The GameServer never reads it (client, Auth API or tool tables): a change alone gives nothing to swap.</summary>
        NotServer,
    }

    /// <summary>
    /// The reload class of every config table (spec 11.7). A table missing here fails <c>TableReloadPolicyTests</c>, so a
    /// new table gets a decision. Hot tables marked <see cref="FixedRows"/> must keep the same row ids in the same order
    /// (and the same netIds) in a reload: their rows index runtime arrays (action indexes and cooldowns, build-up meters) or
    /// are looked up by id from live state (statuses, monsters, classes).
    /// </summary>
    public static class TableReloadPolicy
    {
        private static readonly Dictionary<Type, ReloadClass> Classes = new Dictionary<Type, ReloadClass>
        {
            // Hot: the action, shape, damage, status and monster tuning of backlog K.
            { typeof(ActionDef), ReloadClass.Hot },
            { typeof(ActionEffectDef), ReloadClass.Hot },
            { typeof(HitShapeDef), ReloadClass.Hot },
            { typeof(ProjectileDef), ReloadClass.Hot },
            { typeof(ActionBindings), ReloadClass.Hot },
            { typeof(DamageConstants), ReloadClass.Hot },
            { typeof(RankFactor), ReloadClass.Hot },
            { typeof(ElementCaps), ReloadClass.Hot },
            { typeof(ResourceRegen), ReloadClass.Hot },
            { typeof(ClassDef), ReloadClass.Hot },
            // Like ClassDef: a character built after the reload (its next login) gets the new coefficients (WP-M2-2).
            { typeof(AttributeFormula), ReloadClass.Hot },
            { typeof(MonsterDef), ReloadClass.Hot },
            { typeof(MonsterAiSettings), ReloadClass.Hot },
            { typeof(StatusEffectDef), ReloadClass.Hot },
            { typeof(StatusModifierDef), ReloadClass.Hot },
            { typeof(StatusSettings), ReloadClass.Hot },
            { typeof(BuildUpDef), ReloadClass.Hot },
            { typeof(EffectBuildUpDef), ReloadClass.Hot },

            // Restart: tick rate and charge pools, network and session limits, pools sized at start, the world's regions and
            // spawn points, items and loot (InventoryService and its saves), stats (stat blocks are sized by them).
            { typeof(StartingValuesM1), ReloadClass.Restart },
            { typeof(NetSecurity), ReloadClass.Restart },
            { typeof(EventBusLimits), ReloadClass.Restart },
            { typeof(TelemetrySettings), ReloadClass.Restart },
            { typeof(ServerLoop), ReloadClass.Restart },
            { typeof(RegionDefaults), ReloadClass.Restart },
            { typeof(RegionDef), ReloadClass.Restart },
            { typeof(SessionSettings), ReloadClass.Restart },
            { typeof(StatDef), ReloadClass.Restart },
            { typeof(ProjectileSettings), ReloadClass.Restart },
            { typeof(SpawnPoints), ReloadClass.Restart },
            { typeof(ItemDef), ReloadClass.Restart },
            { typeof(RarityDef), ReloadClass.Restart },
            { typeof(ContainerDef), ReloadClass.Restart },
            { typeof(EquipSlotDef), ReloadClass.Restart },
            { typeof(ItemStatBonusDef), ReloadClass.Restart },
            { typeof(MaterialTierDef), ReloadClass.Restart },
            { typeof(MaterialTagDef), ReloadClass.Restart },
            { typeof(MaterialStatDef), ReloadClass.Restart },
            { typeof(ToolDef), ReloadClass.Restart },
            { typeof(ClassGearDef), ReloadClass.Restart },
            { typeof(LootTableDef), ReloadClass.Restart },
            { typeof(LootTableEntry), ReloadClass.Restart },
            { typeof(LootSettings), ReloadClass.Restart },
            { typeof(GroundAreaSettings), ReloadClass.Restart },
            // Client prediction resolves body collision with the same values (R-2.2-07); the client reads tables only at start.
            { typeof(BodyCollisionSettings), ReloadClass.Restart },
            { typeof(ChatSettings), ReloadClass.Restart },
            // The world and the client read the TOX and SAT caps and decay rates at start (R-3.4-03, WP-M2-2).
            { typeof(ConsumptionMeterSettings), ReloadClass.Restart },
            { typeof(ReliableMessageCaps), ReloadClass.Restart },
            { typeof(InventorySettings), ReloadClass.Restart },
            { typeof(DevTools), ReloadClass.Restart },
            // The admin command worker reads its poll and expiry times at start (the admin panel reads the rest).
            { typeof(AdminSettings), ReloadClass.Restart },
            // The economy (spec 4.2, WP-M2-4): NPCs are placed at start; gold and demand live in services built at start.
            // TODO(spec 12.5): live kill-switches for economy values need a hot path for these.
            { typeof(NpcDef), ReloadClass.Restart },
            { typeof(NpcVendorStock), ReloadClass.Restart },
            { typeof(PriceBase), ReloadClass.Restart },
            { typeof(RarityMult), ReloadClass.Restart },
            { typeof(PriceFloorRecovery), ReloadClass.Restart },
            { typeof(DemandCurve), ReloadClass.Restart },
            { typeof(DemandSettings), ReloadClass.Restart },
            { typeof(EconomySettings), ReloadClass.Restart },
            // Progression (spec 3.6, WP-M2-3): built once into ProgressionRules at start. MonsterDef's level and exp stay Hot.
            { typeof(ProgressionSettings), ReloadClass.Restart },
            { typeof(ExpCurve), ReloadClass.Restart },
            { typeof(ExpBandCurves), ReloadClass.Restart },
            { typeof(RankModifier), ReloadClass.Restart },
            { typeof(RankLevelRanges), ReloadClass.Restart },
            { typeof(AchievementDef), ReloadClass.Restart },
            { typeof(RankRequirements), ReloadClass.Restart },
            // Skill tree (spec 3.5, WP-M2-5): built once into the SkillCatalog at start; learned nodes and the hotbar live in
            // SkillTreeService. TODO(J): a hot path for skill tuning (SkillDef, SkillModifierDef) with a per-player loadout rebuild.
            { typeof(SkillTreeSettings), ReloadClass.Restart },
            { typeof(SkillTree), ReloadClass.Restart },
            { typeof(SkillEvolution), ReloadClass.Restart },
            { typeof(SkillDef), ReloadClass.Restart },
            { typeof(SkillModifierDef), ReloadClass.Restart },
            // The Bystial book (spec 14.2, WP-M2-8): built once into BystialRules at start; the cards a book holds stay.
            { typeof(BystialCardDef), ReloadClass.Restart },
            { typeof(BystialSlots), ReloadClass.Restart },
            { typeof(BystialFamilyBonus), ReloadClass.Restart },
            { typeof(BystialStatCap), ReloadClass.Restart },
            { typeof(BystialEffectCap), ReloadClass.Restart },
            // Outpost quests and runs (spec 5.5, WP-M2-6): built once into QuestRules at start; boards and runs keep their numbers.
            { typeof(QuestCaps), ReloadClass.Restart },
            { typeof(QuestBoardSettings), ReloadClass.Restart },
            { typeof(RunBonus), ReloadClass.Restart },
            { typeof(ForfeitPenalty), ReloadClass.Restart },
            { typeof(OutpostQuestDef), ReloadClass.Restart },
            { typeof(QuestItemDrop), ReloadClass.Restart },
            // Resource nodes (spec 5.2, WP-M3-4): nodes are placed at start and the demand loop's node counters are built then.
            { typeof(GatherSettings), ReloadClass.Restart },
            { typeof(NodeDef), ReloadClass.Restart },
            { typeof(GatherNodes), ReloadClass.Restart },

            // NotServer: the client, the Auth API and the tools.
            { typeof(CharacterSlots), ReloadClass.NotServer },
            { typeof(CharacterCreationConfig), ReloadClass.NotServer },
            { typeof(AuthApiLimits), ReloadClass.NotServer },
            { typeof(ClientPrediction), ReloadClass.NotServer },
            { typeof(ClientCamera), ReloadClass.NotServer },
            { typeof(LoginSettings), ReloadClass.NotServer },
            { typeof(LoginScreens), ReloadClass.NotServer },
            { typeof(LoginLayout), ReloadClass.NotServer },
            { typeof(ServerDirectory), ReloadClass.NotServer },
            { typeof(ReasonCodeStrings), ReloadClass.NotServer },
            { typeof(UiStrings), ReloadClass.NotServer },
            { typeof(EntityVisualDef), ReloadClass.NotServer },
            { typeof(BuffIconRegistry), ReloadClass.NotServer },
            { typeof(HudSettings), ReloadClass.NotServer },
            { typeof(HudLayout), ReloadClass.NotServer },
            { typeof(HudThemeTokens), ReloadClass.NotServer },
            { typeof(Hotkeys), ReloadClass.NotServer },
            { typeof(ItemIconRegistry), ReloadClass.NotServer },
            { typeof(BystialCardArt), ReloadClass.NotServer },
            { typeof(MinimapSettings), ReloadClass.NotServer },
            { typeof(MinimapIconRegistry), ReloadClass.NotServer },
            { typeof(MinimapMarkerDef), ReloadClass.NotServer },
            { typeof(MinimapRegionTint), ReloadClass.NotServer },
            { typeof(InventoryWindowSettings), ReloadClass.NotServer },
            { typeof(CharacterSheetStats), ReloadClass.NotServer },
            { typeof(TargetLockSettings), ReloadClass.NotServer },
            { typeof(BotLinkProfile), ReloadClass.NotServer },
            { typeof(BotBehaviour), ReloadClass.NotServer },
            { typeof(BotScenario), ReloadClass.NotServer },
            // The tutorial's schedule and prompt timings (spec 12.3, WP-M2-10): client display only.
            { typeof(UnlockSchedule), ReloadClass.NotServer },
            { typeof(TutorialSettings), ReloadClass.NotServer },
            { typeof(EconomySimScenario), ReloadClass.NotServer },
        };

        private static readonly HashSet<Type> Fixed = new HashSet<Type>
        {
            typeof(ActionDef), typeof(StatusEffectDef), typeof(BuildUpDef), typeof(MonsterDef), typeof(ClassDef),
        };

        /// <summary>True when the table has a reload class (every table in <see cref="ConfigTables.All"/> must).</summary>
        public static bool IsClassified(Type rowType) => Classes.ContainsKey(rowType);

        public static ReloadClass ClassOf(Type rowType) =>
            Classes.TryGetValue(rowType, out var value)
                ? value
                : throw new ArgumentException($"{rowType.Name} has no reload class (TableReloadPolicy).", nameof(rowType));

        /// <summary>Hot tables whose reload keeps the same row ids, in the same order, with the same netIds.</summary>
        public static bool FixedRows(Type rowType) => Fixed.Contains(rowType);

        /// <summary>The Hot tables in <see cref="ConfigTables.All"/> order: what the reload hash covers (<see cref="TableHash"/>).</summary>
        public static IReadOnlyList<Type> HotTables()
        {
            var hot = new List<Type>();
            foreach (var type in ConfigTables.All)
            {
                if (Classes.TryGetValue(type, out var value) && value == ReloadClass.Hot)
                    hot.Add(type);
            }
            return hot;
        }
    }
}
