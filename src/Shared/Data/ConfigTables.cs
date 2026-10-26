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
            typeof(ItemDef),
            typeof(RarityDef),
            typeof(ContainerDef),
            typeof(LootTableDef),
            typeof(LootTableEntry),
            typeof(LootSettings),
            typeof(StatusEffectDef),
            typeof(StatusModifierDef),
            typeof(BuffIconRegistry),
            typeof(BuildUpDef),
            typeof(EffectBuildUpDef),
            typeof(GroundAreaSettings),
            typeof(HudSettings),
            typeof(HudLayout),
            typeof(HudThemeTokens),
            typeof(ChatSettings),
        };
    }
}
