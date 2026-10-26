#nullable enable
using System;
using Shared.Protocol;

namespace Game.UI.ViewModels.Hud
{
    /// <summary>Every HUD view model (spec 9.9: one per element), filled by <c>HudFeedBinder</c>, drawn by the presenters.</summary>
    public sealed class HudViewModels
    {
        public HudViewModels(int damageNumberPool, int namePlatePool, int chatHistoryLines, int chatMaxChars)
        {
            Chat = new ChatViewModel(chatHistoryLines, chatMaxChars);
            DamageNumbers = new DamageNumberPool(damageNumberPool);
            NamePlates = new NamePlateSet(namePlatePool);
        }

        public ResourcePanelViewModel Resources { get; } = new ResourcePanelViewModel();
        public HotbarViewModel Hotbar { get; } = new HotbarViewModel();
        public ChargesViewModel Charges { get; } = new ChargesViewModel();
        public BuffRowViewModel BuffRow { get; } = new BuffRowViewModel();
        public BossBarViewModel BossBar { get; } = new BossBarViewModel();
        public CastBarViewModel CastBar { get; } = new CastBarViewModel();
        public ToastViewModel Toast { get; } = new ToastViewModel();
        public DamageNumberPool DamageNumbers { get; }
        public NamePlateSet NamePlates { get; }
        public TelegraphSet Telegraphs { get; } = new TelegraphSet();
        public ChatViewModel Chat { get; }

        /// <summary>The connection ended: nothing shown is current any more.</summary>
        public void Clear()
        {
            Resources.Clear();
            Hotbar.Clear();
            Charges.Clear();
            BuffRow.Clear();
            BossBar.Hide();
            CastBar.Hide();
            Toast.Hide();
            DamageNumbers.Clear();
            NamePlates.Clear();
            Telegraphs.Clear();
            Chat.Clear();
        }
    }

    /// <summary>The own HP, MP and stamina with their maxima (spec 9.3 <c>resource_panel</c>), from the snapshot header.</summary>
    public sealed class ResourcePanelViewModel : ViewModel
    {
        public bool HasValues { get; private set; }
        public int Hp { get; private set; }
        public int MaxHp { get; private set; }
        public int Mp { get; private set; }
        public int MaxMp { get; private set; }
        public int Stamina { get; private set; }
        public int MaxStamina { get; private set; }

        public void Set(in OwnResources own)
        {
            if (HasValues && own.Hp == Hp && own.MaxHp == MaxHp && own.Mp == Mp && own.MaxMp == MaxMp
                && own.Stamina == Stamina && own.MaxStamina == MaxStamina)
                return;
            HasValues = true;
            Hp = own.Hp;
            MaxHp = own.MaxHp;
            Mp = own.Mp;
            MaxMp = own.MaxMp;
            Stamina = own.Stamina;
            MaxStamina = own.MaxStamina;
            NotifyChanged();
        }

        public void Clear()
        {
            if (!HasValues)
                return;
            HasValues = false;
            NotifyChanged();
        }

        /// <summary>Bar fill 0 to 1; 0 for a zero maximum.</summary>
        public static float Fraction(int value, int max) => max <= 0 ? 0f : Math.Max(0f, Math.Min(1f, value / (float)max));
    }

    /// <summary>One charge square (spec 9.3 <c>parry_dodge</c>): count, maximum, and when the next charge comes back.</summary>
    public readonly struct ChargeView : IEquatable<ChargeView>
    {
        public ChargeView(int count, int max, double nextChargeAtMs, double rechargeMs)
        {
            Count = count;
            Max = max;
            NextChargeAtMs = nextChargeAtMs;
            RechargeMs = rechargeMs;
        }

        public int Count { get; }
        public int Max { get; }

        /// <summary>Display time of the next charge; 0 when full.</summary>
        public double NextChargeAtMs { get; }

        public double RechargeMs { get; }

        public bool Equals(ChargeView other) =>
            Count == other.Count && Max == other.Max && NextChargeAtMs == other.NextChargeAtMs && RechargeMs == other.RechargeMs;

        public override bool Equals(object? obj) => obj is ChargeView other && Equals(other);

        public override int GetHashCode() => (Count << 16) ^ Max ^ NextChargeAtMs.GetHashCode();

        /// <summary>Same count and maximum, and a recharge end within <paramref name="toleranceMs"/> (one tick of jitter).</summary>
        public bool SameAs(in ChargeView other, double toleranceMs) =>
            Count == other.Count && Max == other.Max && RechargeMs == other.RechargeMs
            && (NextChargeAtMs == 0) == (other.NextChargeAtMs == 0)
            && Math.Abs(NextChargeAtMs - other.NextChargeAtMs) <= toleranceMs;

        /// <summary>Recharge progress of the next charge, 0 to 1 (1 when full).</summary>
        public float Progress(double nowMs)
        {
            if (NextChargeAtMs <= 0 || RechargeMs <= 0)
                return 1f;
            double left = NextChargeAtMs - nowMs;
            return (float)Math.Max(0.0, Math.Min(1.0, 1.0 - left / RechargeMs));
        }
    }

    /// <summary>
    /// Parry and dodge charges (spec 9.3, 9.4: the client recomputes the recharge from timestamps), from the predicted
    /// <c>ChargeMeter</c>s. Raises <see cref="ViewModel.Changed"/> only when a count changes or a recharge end moves.
    /// </summary>
    public sealed class ChargesViewModel : ViewModel
    {
        public ChargeView Parry { get; private set; }
        public ChargeView Dodge { get; private set; }
        public bool HasValues { get; private set; }

        public void Set(in ChargeView parry, in ChargeView dodge, double toleranceMs)
        {
            if (HasValues && parry.SameAs(Parry, toleranceMs) && dodge.SameAs(Dodge, toleranceMs))
                return;
            HasValues = true;
            Parry = parry;
            Dodge = dodge;
            NotifyChanged();
        }

        public void Clear()
        {
            if (!HasValues)
                return;
            HasValues = false;
            Parry = default;
            Dodge = default;
            NotifyChanged();
        }
    }

    /// <summary>The local player's channel (spec 9.3 <c>cast_bar</c>): its action, and when it started and ends.</summary>
    public sealed class CastBarViewModel : ViewModel
    {
        public bool Visible { get; private set; }
        public string ActionId { get; private set; } = "";
        public double StartMs { get; private set; }
        public double EndMs { get; private set; }

        public void Show(string actionId, double startMs, double endMs, double toleranceMs)
        {
            if (Visible && actionId == ActionId && Math.Abs(endMs - EndMs) <= toleranceMs)
                return;
            Visible = true;
            ActionId = actionId;
            StartMs = startMs;
            EndMs = endMs;
            NotifyChanged();
        }

        public void Hide()
        {
            if (!Visible)
                return;
            Visible = false;
            NotifyChanged();
        }

        /// <summary>Channel progress 0 to 1 at <paramref name="nowMs"/>.</summary>
        public float Progress(double nowMs) =>
            EndMs <= StartMs ? 1f : (float)Math.Max(0.0, Math.Min(1.0, (nowMs - StartMs) / (EndMs - StartMs)));
    }

    /// <summary>
    /// The refusal or parry result toast (spec 9.4 <c>ActionResult</c>): the reason code whose <c>ReasonCodeStrings</c> text
    /// shows, for a while. A new result replaces the shown one.
    /// </summary>
    public sealed class ToastViewModel : ViewModel
    {
        public bool Visible { get; private set; }
        public string ReasonCode { get; private set; } = "";
        public bool IsParryResult { get; private set; }
        public double UntilMs { get; private set; }

        /// <summary>Increases with every toast, so a repeat of the same reason still restarts the presenter's effect.</summary>
        public int Sequence { get; private set; }

        public void Show(string reasonCode, bool isParryResult, double nowMs, double durationMs)
        {
            Visible = true;
            ReasonCode = reasonCode;
            IsParryResult = isParryResult;
            UntilMs = nowMs + durationMs;
            Sequence++;
            NotifyChanged();
        }

        public void Tick(double nowMs)
        {
            if (Visible && nowMs >= UntilMs)
                Hide();
        }

        public void Hide()
        {
            if (!Visible)
                return;
            Visible = false;
            NotifyChanged();
        }
    }

    /// <summary>
    /// The boss bar (spec 9.3 <c>boss_bar</c>). Engagement (user decision 2026-10-24, spec 3.1): the player hit the boss or
    /// the boss hit the player within the timeout (<c>HudSettings.bossEngageTimeoutMs</c>); the bar hides when that passes,
    /// when the boss dies, or when it leaves interest. A display rule built from server facts (hit messages); the server
    /// decides every hit.
    /// </summary>
    public sealed class BossBarViewModel : ViewModel
    {
        public bool Visible { get; private set; }
        public uint BossId { get; private set; }
        public string MonsterId { get; private set; } = "";
        public Shared.Simulation.Stats.Rank Rank { get; private set; }

        /// <summary>The boss's HP bucket (spec 9.4: 0 to <see cref="Snapshot.TopHpBucket"/>); the bar shows no numbers.</summary>
        public byte HpBucket { get; private set; } = Snapshot.TopHpBucket;

        public double LastCombatMs { get; private set; }

        public float HpFraction => HpBucket / (float)Snapshot.TopHpBucket;

        /// <summary>A hit between the player and a boss at <paramref name="nowMs"/>: engages it (or the newer boss).</summary>
        public void Engage(uint bossId, string monsterId, Shared.Simulation.Stats.Rank rank, byte hpBucket, double nowMs)
        {
            LastCombatMs = nowMs;
            if (Visible && bossId == BossId && hpBucket == HpBucket)
                return;
            Visible = true;
            BossId = bossId;
            MonsterId = monsterId;
            Rank = rank;
            HpBucket = hpBucket;
            NotifyChanged();
        }

        public void SetHp(byte hpBucket)
        {
            if (!Visible || hpBucket == HpBucket)
                return;
            HpBucket = hpBucket;
            NotifyChanged();
        }

        /// <summary>Hides the bar when the engagement timed out, or the boss died or left interest.</summary>
        public void Tick(double nowMs, double timeoutMs, bool bossPresent, bool bossDead)
        {
            if (Visible && (!bossPresent || bossDead || nowMs - LastCombatMs > timeoutMs))
                Hide();
        }

        public void Hide()
        {
            if (!Visible)
                return;
            Visible = false;
            NotifyChanged();
        }
    }
}
