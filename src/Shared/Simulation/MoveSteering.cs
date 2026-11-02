using System;

namespace Shared.Simulation
{
    /// <summary>
    /// Mouse-relative movement (R-2.2-01, M3 decision 14): the eight key directions (W, A, S, D and their pairs, world
    /// axes) are turned as one by the offset between the facing and the compass sector it lies in, so the key whose
    /// sector holds the facing runs exactly at it, the opposite key exactly away, and every key stays within half a sector
    /// (plus the hysteresis) of its own direction. Integer binary angles (65536 = one turn, 0 = +Y, clockwise), pure:
    /// <see cref="MovementSim"/>, the dodge (<c>ActionMachine.DashHeading</c>), the bots and the QA run read the same numbers.
    /// </summary>
    public static class MoveSteering
    {
        /// <summary>Compass sectors: one per key direction.</summary>
        public const int Sectors = 8;

        /// <summary>45 degrees as a binary angle.</summary>
        public const int SectorAngle = 65536 / Sectors;

        /// <summary>22.5 degrees: the farthest a facing lies from its nearest sector's centre.</summary>
        public const int HalfSector = SectorAngle / 2;

        private const int SectorShift = 13; // 65536 / 8 = 1 << 13

        // Direction index clockwise from world +Y: 0 N (W), 1 NE, 2 E (D), 3 SE, 4 S, 5 SW, 6 W (A), 7 NW.
        private static readonly MoveKeys[] KeysForDirection =
        {
            MoveKeys.W, MoveKeys.W | MoveKeys.D, MoveKeys.D, MoveKeys.S | MoveKeys.D,
            MoveKeys.S, MoveKeys.S | MoveKeys.A, MoveKeys.A, MoveKeys.W | MoveKeys.A,
        };

        /// <summary>
        /// <c>StartingValuesM1.moveKeyHysteresisDeg</c> as a binary angle, rounded to nearest. The table's range keeps it
        /// below <see cref="HalfSector"/>.
        /// </summary>
        public static ushort HysteresisFrom(int degrees)
        {
            if (degrees < 0 || degrees * 65536L >= HalfSector * 360L)
                throw new ArgumentOutOfRangeException(nameof(degrees), "must be 0 or more and below 22.5 degrees");
            return (ushort)((degrees * 65536L + 180) / 360);
        }

        /// <summary>The compass sector (0..7) whose centre is nearest <paramref name="angle"/>; an exact half rounds clockwise.</summary>
        public static int Nearest(ushort angle) => ((angle + HalfSector) >> SectorShift) & (Sectors - 1);

        /// <summary>
        /// The sector (0..7) the keys turn from at <paramref name="facing"/>: the previous one (a <see cref="MovementState.KeySector"/>,
        /// 0 for none) while the facing stays within half a sector plus <paramref name="hysteresis"/> of its centre (a tie
        /// keeps it), else the nearest. A mouse on a sector edge so cannot flip a held key between two turns each tick.
        /// </summary>
        public static int Sector(ushort facing, byte previous, ushort hysteresis)
        {
            if (previous >= 1 && previous <= Sectors)
            {
                int kept = previous - 1;
                int offset = Math.Abs((int)unchecked((short)(ushort)(facing - kept * SectorAngle)));
                if (offset <= HalfSector + hysteresis)
                    return kept;
            }
            return Nearest(facing);
        }

        /// <summary><see cref="Sector"/> as the next <see cref="MovementState.KeySector"/> (1..8).</summary>
        public static byte NextKeySector(ushort facing, byte previous, ushort hysteresis) =>
            (byte)(Sector(facing, previous, hysteresis) + 1);

        /// <summary>
        /// The turn the keys get at <paramref name="facing"/> from <paramref name="keySector"/> (a <see cref="MovementState.KeySector"/>;
        /// 0 takes the nearest sector): the facing minus the sector's centre, signed, within half a sector plus the hysteresis.
        /// </summary>
        public static short Turn(ushort facing, byte keySector)
        {
            int sector = keySector >= 1 && keySector <= Sectors ? keySector - 1 : Nearest(facing);
            return unchecked((short)(ushort)(facing - sector * SectorAngle));
        }

        /// <summary>The direction index (0..7) of the held keys; -1 when none are held or opposite keys cancel out.</summary>
        public static int DirectionOf(MoveKeys keys)
        {
            int dx = ((keys & MoveKeys.D) != 0 ? 1 : 0) - ((keys & MoveKeys.A) != 0 ? 1 : 0);
            int dy = ((keys & MoveKeys.W) != 0 ? 1 : 0) - ((keys & MoveKeys.S) != 0 ? 1 : 0);
            if (dx == 0 && dy == 0)
                return -1;
            if (dx == 0)
                return dy > 0 ? 0 : 4;
            if (dy == 0)
                return dx > 0 ? 2 : 6;
            if (dy > 0)
                return dx > 0 ? 1 : 7;
            return dx > 0 ? 3 : 5;
        }

        /// <summary>The keys of direction index <paramref name="direction"/> (any integer, taken modulo 8).</summary>
        public static MoveKeys KeysFor(int direction) => KeysForDirection[((direction % Sectors) + Sectors) % Sectors];

        /// <summary>
        /// The heading of <paramref name="keys"/> turned by <paramref name="turn"/> (<see cref="Turn"/>); false when no
        /// direction is held.
        /// </summary>
        public static bool TryHeading(MoveKeys keys, short turn, out ushort heading)
        {
            int direction = DirectionOf(keys);
            heading = 0;
            if (direction < 0)
                return false;
            heading = unchecked((ushort)(direction * SectorAngle + turn));
            return true;
        }

        /// <summary>
        /// The inverse of the step: the keys whose turned heading at <paramref name="facing"/> (with the sector chosen from
        /// <paramref name="previous"/> and <paramref name="hysteresis"/>, as <see cref="MovementSim.Step(in MovementState, in MovementInput, bool, int, int, ushort)"/>
        /// chooses it) is nearest <paramref name="wanted"/>. With <paramref name="wanted"/> equal to the facing they run
        /// exactly along it; otherwise within half a sector.
        /// </summary>
        public static MoveKeys KeysToward(ushort wanted, ushort facing, byte previous, ushort hysteresis)
        {
            int sector = Sector(facing, previous, hysteresis);
            short turn = unchecked((short)(ushort)(facing - sector * SectorAngle));
            return KeysForDirection[Nearest(unchecked((ushort)(wanted - turn)))];
        }
    }
}
