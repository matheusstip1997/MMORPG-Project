using System;

namespace Shared.Simulation.Actions
{
    /// <summary>
    /// A charge pool (spec 3.3 <c>ChargeMeter</c>: <c>{charges, max, lastChangeTick, rechargeMs}</c>, R-3.3-02, R-3.3-03):
    /// while below <see cref="Max"/> it gains one charge every <see cref="RechargeTicks"/>, computed on read from ticks, so
    /// it needs no per-tick update. Integer only. <see cref="LastChangeTick"/> is where the running recharge started:
    /// spending from a full pool starts the recharge, spending from a recharging pool keeps the progress towards the next
    /// charge. Ticks are those of the caller's clock (<see cref="ActionMachineState.Tick"/>); a tick before
    /// <see cref="LastChangeTick"/> adds nothing.
    /// </summary>
    public struct ChargeMeter : IEquatable<ChargeMeter>
    {
        /// <summary>A full pool.</summary>
        /// <param name="rechargeTicks">Ticks per charge; 0 or less keeps the pool full.</param>
        public ChargeMeter(int max, int rechargeTicks)
        {
            if (max < 0)
                throw new ArgumentOutOfRangeException(nameof(max), "must not be negative");
            Max = max;
            RechargeTicks = rechargeTicks;
            StoredCharges = max;
            LastChangeTick = 0;
        }

        public int Max { get; }

        public int RechargeTicks { get; }

        /// <summary>Charges at <see cref="LastChangeTick"/>, before the recharge since then.</summary>
        public int StoredCharges { get; private set; }

        public uint LastChangeTick { get; private set; }

        /// <summary>Charges at <paramref name="tick"/>.</summary>
        public int ChargesAt(uint tick)
        {
            if (StoredCharges >= Max || RechargeTicks <= 0)
                return Max;
            int gained = Gained(tick);
            return gained >= Max - StoredCharges ? Max : StoredCharges + gained;
        }

        /// <summary>Ticks from <paramref name="tick"/> until the next charge; 0 when full (for the HUD).</summary>
        public int TicksUntilNextCharge(uint tick)
        {
            if (ChargesAt(tick) >= Max)
                return 0;
            return RechargeTicks - Elapsed(tick) % RechargeTicks;
        }

        /// <summary>Spends one charge at <paramref name="tick"/>; false, changing nothing, when there is none.</summary>
        public bool TrySpend(uint tick)
        {
            int charges = ChargesAt(tick);
            if (charges <= 0)
                return false;
            Settle(tick, charges);
            StoredCharges = charges - 1;
            return true;
        }

        /// <summary>
        /// Gives one charge back at <paramref name="tick"/> (client prediction undoing a spend the server did not make);
        /// the progress towards the next charge is kept. Nothing when full.
        /// </summary>
        public void Refund(uint tick)
        {
            int charges = ChargesAt(tick);
            if (charges >= Max)
                return;
            Settle(tick, charges);
            StoredCharges = charges + 1;
        }

        /// <summary>Fills the pool (spawn, respawn).</summary>
        public void Fill(uint tick)
        {
            StoredCharges = Max;
            LastChangeTick = tick;
        }

        public bool Equals(ChargeMeter other) =>
            Max == other.Max && RechargeTicks == other.RechargeTicks && StoredCharges == other.StoredCharges
            && LastChangeTick == other.LastChangeTick;

        public override bool Equals(object? obj) => obj is ChargeMeter other && Equals(other);

        public override int GetHashCode() => (StoredCharges << 24) ^ (Max << 16) ^ RechargeTicks ^ (int)LastChangeTick;

        public override string ToString() => $"{StoredCharges}/{Max} at {LastChangeTick}, 1 per {RechargeTicks} ticks";

        /// <summary>Moves the anchor to <paramref name="tick"/> keeping the progress towards the next charge.</summary>
        private void Settle(uint tick, int charges)
        {
            if (charges >= Max || RechargeTicks <= 0)
            {
                LastChangeTick = tick; // full: the recharge starts now
            }
            else
            {
                int gained = charges - StoredCharges;
                LastChangeTick = unchecked(LastChangeTick + (uint)(gained * RechargeTicks));
            }
        }

        private int Gained(uint tick) => Elapsed(tick) / RechargeTicks;

        private int Elapsed(uint tick)
        {
            int elapsed = unchecked((int)(tick - LastChangeTick));
            return elapsed > 0 ? elapsed : 0;
        }
    }
}
