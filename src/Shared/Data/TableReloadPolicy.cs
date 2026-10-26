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
            { typeof(MonsterDef), ReloadClass.Hot },
            { typeof(MonsterAiSettings), ReloadClass.Hot },
            { typeof(StatusEffectDef), ReloadClass.Hot },
            { typeof(StatusModifierDef), ReloadClass.Hot },
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
            { typeof(LootTableDef), ReloadClass.Restart },
            { typeof(LootTableEntry), ReloadClass.Restart },
            { typeof(LootSettings), ReloadClass.Restart },
            { typeof(GroundAreaSettings), ReloadClass.Restart },
            { typeof(ChatSettings), ReloadClass.Restart },
            { typeof(ReliableMessageCaps), ReloadClass.Restart },
            { typeof(InventorySettings), ReloadClass.Restart },
            { typeof(DevTools), ReloadClass.Restart },

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
            { typeof(InventoryWindowSettings), ReloadClass.NotServer },
            { typeof(BotLinkProfile), ReloadClass.NotServer },
            { typeof(BotBehaviour), ReloadClass.NotServer },
            { typeof(BotScenario), ReloadClass.NotServer },
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
