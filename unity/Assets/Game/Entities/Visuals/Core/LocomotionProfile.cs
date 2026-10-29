#nullable enable
using System;

namespace Game.Gameplay.Entities.Visuals
{
    /// <summary>One directional locomotion loop as the blend sees it: its length, whether it plays backward, its reference speed.</summary>
    public readonly struct LocomotionSlot
    {
        public LocomotionSlot(float lengthS, bool reverse, float referenceCmPerSec)
        {
            LengthS = Math.Max(0.001f, lengthS);
            Reverse = reverse;
            ReferenceCmPerSec = referenceCmPerSec;
        }

        public float LengthS { get; }

        public bool Reverse { get; }

        /// <summary>Ground speed at which the loop's feet stay planted, at the size the body is shown at.</summary>
        public float ReferenceCmPerSec { get; }
    }

    /// <summary>
    /// The locomotion of one shown body (pure, no Unity): the idle length, the eight directional loops (walk and run x
    /// <see cref="LocomotionDirection"/>; a direction without its own loop repeats the forward one) and the leg turn. Built
    /// from an <see cref="AnimationSet"/> by <see cref="EntityVisual"/>, with the reference speeds already scaled to the
    /// row's size.
    /// </summary>
    public sealed class LocomotionProfile
    {
        public const int Directions = 4;

        /// <summary>Walk slots are 0..3, run slots 4..7, each in <see cref="LocomotionDirection"/> order.</summary>
        public const int Slots = 2 * Directions;

        private readonly LocomotionSlot[] _slots;

        /// <param name="slots"><see cref="Slots"/> entries in <see cref="SlotIndex"/> order. Without a run loop the run slots repeat the walk ones.</param>
        public LocomotionProfile(float idleLengthS, LocomotionSlot[] slots, float legTurnMaxDeg, float legTurnDegPerSec, float legTurnHysteresisDeg)
        {
            if (slots.Length != Slots)
                throw new ArgumentException($"expected {Slots} slots, got {slots.Length}", nameof(slots));
            IdleLengthS = Math.Max(0.001f, idleLengthS);
            _slots = (LocomotionSlot[])slots.Clone();
            LegTurnMaxDeg = Math.Max(0f, Math.Min(90f, legTurnMaxDeg));
            LegTurnDegPerSec = Math.Max(0f, legTurnDegPerSec);
            LegTurnHysteresisDeg = Math.Max(0f, Math.Min(45f, legTurnHysteresisDeg));
        }

        public float IdleLengthS { get; }

        /// <summary>0: the legs never turn (the body has side loops, or no hips).</summary>
        public float LegTurnMaxDeg { get; }

        public float LegTurnDegPerSec { get; }

        public float LegTurnHysteresisDeg { get; }

        public static int SlotIndex(bool walk, int direction) => (walk ? 0 : Directions) + direction;

        public LocomotionSlot Slot(int index) => _slots[index];

        public LocomotionSlot Slot(bool walk, int direction) => _slots[SlotIndex(walk, direction)];

        /// <summary>A body with forward loops only (every direction repeats them) and no leg turn.</summary>
        /// <param name="runLengthS">Length of the run loop, or of the walk loop when there is none (then <paramref name="runReferenceCmPerSec"/> equals the walk's).</param>
        public static LocomotionProfile ForwardOnly(float idleLengthS, float walkLengthS, float runLengthS, float walkReferenceCmPerSec, float runReferenceCmPerSec)
        {
            var slots = new LocomotionSlot[Slots];
            for (int direction = 0; direction < Directions; direction++)
            {
                slots[SlotIndex(true, direction)] = new LocomotionSlot(walkLengthS, false, walkReferenceCmPerSec);
                slots[SlotIndex(false, direction)] = new LocomotionSlot(runLengthS, false, runReferenceCmPerSec);
            }
            return new LocomotionProfile(idleLengthS, slots, 0f, 0f, 0f);
        }
    }
}
