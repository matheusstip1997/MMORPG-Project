using System;

namespace Shared.Simulation.Stats
{
    /// <summary>
    /// HP, MP and stamina of one entity (spec 3.4 R-3.4-03). Max and regeneration rate are stats of the entity's
    /// <see cref="StatBlock"/> (<c>maxHp</c>, <c>hpRegen</c>, ...); delays come from <see cref="CombatConfig"/>
    /// (stamina: 1 s after a spend, spec 12.7). HP does not regenerate at 0 (dead); a restore or <see cref="Set"/>
    /// brings it back. Intoxication, satiety and parry charges are other meters (later backlog items).
    /// <para>
    /// The meters keep the max and rate they last read. When the stat block's <see cref="StatBlock.Version"/> changes,
    /// the next call first folds regeneration up to that moment with the old values, then uses the new ones; call
    /// <see cref="Settle"/> right when stats change to make the switch exact. No allocation per call.
    /// </para>
    /// </summary>
    public sealed class ResourceMeters
    {
        private readonly StatBlock _stats;
        private readonly CombatConfig _config;
        private ResourceMeter _hp;
        private ResourceMeter _mp;
        private ResourceMeter _stamina;
        private readonly int[] _max = new int[3];
        private readonly int[] _regen = new int[3];
        private int _statsVersion;

        /// <summary>All three meters start full at <paramref name="nowMs"/>.</summary>
        public ResourceMeters(StatBlock stats, CombatConfig config, long nowMs)
        {
            _stats = stats ?? throw new ArgumentNullException(nameof(stats));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            if (!ReferenceEquals(stats.Rules, config.Stats))
                throw new ArgumentException("the stat block was built from other StatDef rules", nameof(stats));
            ReadStats();
            _hp.Set(_max[(int)ResourceKind.Hp], nowMs);
            _mp.Set(_max[(int)ResourceKind.Mp], nowMs);
            _stamina.Set(_max[(int)ResourceKind.Stamina], nowMs);
        }

        public int Current(ResourceKind kind, long nowMs)
        {
            Settle(nowMs);
            return Meter(kind).Current(nowMs, _max[(int)kind], _regen[(int)kind], RegenAtZero(kind));
        }

        public int Max(ResourceKind kind, long nowMs)
        {
            Settle(nowMs);
            return _max[(int)kind];
        }

        /// <summary>Spends the whole cost or nothing (skill MP, action stamina). Restarts the delay on success.</summary>
        public SpendResult TrySpend(ResourceKind kind, int amount, long nowMs)
        {
            Settle(nowMs);
            return Meter(kind).TrySpend(amount, nowMs, _max[(int)kind], _regen[(int)kind], RegenAtZero(kind), DelayMs(kind));
        }

        /// <summary>Removes up to <paramref name="amount"/> (damage on HP); returns the points removed.</summary>
        public int Drain(ResourceKind kind, int amount, long nowMs)
        {
            Settle(nowMs);
            return Meter(kind).Drain(amount, nowMs, _max[(int)kind], _regen[(int)kind], RegenAtZero(kind), DelayMs(kind));
        }

        /// <summary>Adds up to <paramref name="amount"/>, capped at max; returns the points added.</summary>
        public int Restore(ResourceKind kind, int amount, long nowMs)
        {
            Settle(nowMs);
            return Meter(kind).Restore(amount, nowMs, _max[(int)kind], _regen[(int)kind], RegenAtZero(kind));
        }

        /// <summary>Sets a meter (respawn, load from a save); the value is clamped to max when read.</summary>
        public void Set(ResourceKind kind, int value, long nowMs)
        {
            Settle(nowMs);
            Meter(kind).Set(value, nowMs);
        }

        /// <summary>Folds regeneration up to <paramref name="nowMs"/> and picks up changed max and rate stats.</summary>
        public void Settle(long nowMs)
        {
            _stats.Refresh();
            if (_stats.Version == _statsVersion)
                return;
            for (int kind = 0; kind < 3; kind++)
                Meter((ResourceKind)kind).Settle(nowMs, _max[kind], _regen[kind], RegenAtZero((ResourceKind)kind));
            ReadStats();
        }

        private void ReadStats()
        {
            var ids = _config.Ids;
            _max[(int)ResourceKind.Hp] = _stats.Get(ids.MaxHp);
            _max[(int)ResourceKind.Mp] = _stats.Get(ids.MaxMp);
            _max[(int)ResourceKind.Stamina] = _stats.Get(ids.MaxStamina);
            _regen[(int)ResourceKind.Hp] = _stats.Get(ids.HpRegen);
            _regen[(int)ResourceKind.Mp] = _stats.Get(ids.MpRegen);
            _regen[(int)ResourceKind.Stamina] = _stats.Get(ids.StaminaRegen);
            _statsVersion = _stats.Version;
        }

        private ref ResourceMeter Meter(ResourceKind kind)
        {
            switch (kind)
            {
                case ResourceKind.Hp: return ref _hp;
                case ResourceKind.Mp: return ref _mp;
                case ResourceKind.Stamina: return ref _stamina;
                default: throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }

        private int DelayMs(ResourceKind kind) => kind switch
        {
            ResourceKind.Hp => _config.HpRegenDelayMs,
            ResourceKind.Mp => _config.MpRegenDelayMs,
            _ => _config.StaminaRegenDelayMs,
        };

        private static bool RegenAtZero(ResourceKind kind) => kind != ResourceKind.Hp;
    }
}
