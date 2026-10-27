using Shared.Data.Tables;
using Shared.Simulation.Stats;

namespace GameServer.Stats;

/// <summary>A character's saved toxicity and satiety (columns of V0007): thousandths of a point at a UTC time.</summary>
public readonly record struct SavedConsumption(int ToxicityMilli, int SatietyMilli, long AtUtcMs);

/// <summary>
/// The intoxication and satiety meters of one character (R-3.4-03, WP-M2-2), on UTC time from <c>IClock</c> (not the
/// world's simulation time: they decay across logouts, "computed from timestamps on login"). Caps and decay rates come
/// from <see cref="ConsumptionMeterSettings"/>. Nothing raises them yet (potions and food are M3); saved with every
/// character save. World thread only.
/// </summary>
public sealed class ConsumptionMeters
{
    private readonly ConsumptionMeterSettings _settings;
    private DecayMeter _toxicity;
    private DecayMeter _satiety;

    public ConsumptionMeters(ConsumptionMeterSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>Caps 0 and no decay: meters that stay empty (a world built without the table).</summary>
    public static ConsumptionMeterSettings Empty { get; } = new() { ToxicityMax = 0, SatietyMax = 0 };

    public ConsumptionMeterSettings Settings => _settings;

    public long ToxicityMilli(long utcNowMs) => _toxicity.MilliAt(utcNowMs, _settings.ToxicityDecayPerMinute);

    public long SatietyMilli(long utcNowMs) => _satiety.MilliAt(utcNowMs, _settings.SatietyDecayPerMinute);

    public int Toxicity(long utcNowMs) => _toxicity.Current(utcNowMs, _settings.ToxicityDecayPerMinute);

    public int Satiety(long utcNowMs) => _satiety.Current(utcNowMs, _settings.SatietyDecayPerMinute);

    /// <summary>A saved character: each meter decays from its saved time (a value above the cap is clamped to it).</summary>
    public void Restore(in SavedConsumption saved)
    {
        _toxicity.Set(Math.Min(saved.ToxicityMilli, (long)_settings.ToxicityMax * DecayMeter.MilliPerPoint), saved.AtUtcMs);
        _satiety.Set(Math.Min(saved.SatietyMilli, (long)_settings.SatietyMax * DecayMeter.MilliPerPoint), saved.AtUtcMs);
    }

    /// <summary>What a save writes, at <paramref name="utcNowMs"/>.</summary>
    public SavedConsumption Capture(long utcNowMs) => new(
        (int)Math.Min(int.MaxValue, ToxicityMilli(utcNowMs)),
        (int)Math.Min(int.MaxValue, SatietyMilli(utcNowMs)),
        utcNowMs);

    /// <summary>Raises toxicity, capped at <c>toxicityMax</c> (a potion, M3); returns the whole points gained.</summary>
    public int AddToxicity(int points, long utcNowMs) =>
        _toxicity.Add(points, utcNowMs, _settings.ToxicityMax, _settings.ToxicityDecayPerMinute);

    /// <summary>Raises satiety, capped at <c>satietyMax</c> (food, M3); returns the whole points gained.</summary>
    public int AddSatiety(int points, long utcNowMs) =>
        _satiety.Add(points, utcNowMs, _settings.SatietyMax, _settings.SatietyDecayPerMinute);
}
