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
        public const string HitDodged = "hud.hit.dodged";
        public const string HitParried = "hud.hit.parried";
        public const string HitHeal = "hud.hit.heal";
        public const string BossTitle = "hud.boss.title";
        public const string Stacks = "hud.stacks";

        /// <summary>The key label of an <c>ActionBindings</c> skill slot (spec 9.6): <c>hud.key.slot&lt;n&gt;</c>.</summary>
        public static string KeySlot(int skillSlot) => "hud.key.slot" + skillSlot;
    }
}
