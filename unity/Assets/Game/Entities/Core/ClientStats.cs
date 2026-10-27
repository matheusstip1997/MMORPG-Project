#nullable enable
using System;
using System.Collections.Generic;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Stats;

namespace Game.Gameplay.Entities
{
    /// <summary>
    /// The client's copy of its own final stats and its toxicity and satiety (WP-M2-2, spec 9.4): the last
    /// <see cref="OwnStats"/> the server sent, by <c>StatDef</c> id. The server computes every value (architecture rule 1);
    /// the client only reads them and lets the two meters decay between messages at the <see cref="ConsumptionMeterSettings"/>
    /// rates, from the time the message arrived on the caller's display clock (ms). A message whose stat count is not this
    /// client's <c>StatDef</c> row count (tables out of step) is dropped and counted. Engine free (ClientCore compiles it).
    /// </summary>
    public sealed class ClientStats
    {
        private readonly Dictionary<string, int> _index = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly int[] _values;
        private readonly ConsumptionMeterSettings _meters;
        private DecayMeter _toxicity;
        private DecayMeter _satiety;

        public ClientStats(ConfigSet config)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config));
            int count = 0;
            if (config.TryGet(typeof(StatDef), out var stats))
            {
                foreach (var row in stats.Rows)
                    _index[((StatDef)row).Id] = count++;
            }
            _values = new int[count];
            _meters = config.TryGet(typeof(ConsumptionMeterSettings), out var meters) && meters.Rows.Count == 1
                ? (ConsumptionMeterSettings)meters.Rows[0]
                : new ConsumptionMeterSettings();
        }

        /// <summary>False until the first message of this connection.</summary>
        public bool HasValues { get; private set; }

        /// <summary>The server's stat block version of the last message.</summary>
        public uint Version { get; private set; }

        /// <summary>Messages taken; messages dropped as malformed or out of step with the tables.</summary>
        public long Received { get; private set; }

        public long Dropped { get; private set; }

        public int ToxicityMax => _meters.ToxicityMax;

        public int SatietyMax => _meters.SatietyMax;

        /// <summary>A final stat by <c>StatDef</c> id; 0 before the first message or for an id the table does not have.</summary>
        public int Get(string statId) => HasValues && _index.TryGetValue(statId, out int i) ? _values[i] : 0;

        /// <summary>Toxicity in whole points at <paramref name="nowMs"/> (display clock), rounded up like the server.</summary>
        public int Toxicity(long nowMs) => _toxicity.Current(nowMs, _meters.ToxicityDecayPerMinute);

        public int Satiety(long nowMs) => _satiety.Current(nowMs, _meters.SatietyDecayPerMinute);

        /// <summary>
        /// Takes an <see cref="OwnStats"/> message (true, also when it had to drop it); false for any other message.
        /// <paramref name="nowMs"/> is the display clock the meters decay on.
        /// </summary>
        public bool Apply(ReadOnlySpan<byte> message, long nowMs)
        {
            if (ReliableMessages.PeekId(message) != ReliableMessageId.OwnStats)
                return false;
            if (!StatsCodec.TryRead(message, out var stats) || stats.Values.Length != _values.Length)
            {
                Dropped++;
                return true;
            }
            Array.Copy(stats.Values, _values, _values.Length);
            Version = stats.Version;
            _toxicity.Set(stats.ToxicityMilli, nowMs);
            _satiety.Set(stats.SatietyMilli, nowMs);
            HasValues = true;
            Received++;
            return true;
        }

        /// <summary>A new connection: nothing is known until its first message.</summary>
        public void Reset()
        {
            HasValues = false;
            Version = 0;
            Array.Clear(_values, 0, _values.Length);
            _toxicity = default;
            _satiety = default;
        }
    }
}
