namespace Shared.Data.Tables
{
    /// <summary>
    /// Client camera settings (spec 1.1, R-2.2-05). Client-only. The pitch is fixed at 45 degrees by design and is not
    /// configurable; the player turns the camera horizontally around the character (yaw), and when the turn keys are
    /// released the yaw settles on the nearest 45-degree step so the WASD directions stay exact on the wire.
    /// Values in data/tables/ClientCamera.json are placeholders to tune during Milestone 1.
    /// </summary>
    [Table("ClientCamera", 1)]
    [Singleton]
    public sealed class ClientCamera : IValidatableRow
    {
        /// <summary>Distance from the look-at point to the camera along the 45-degree view ray.</summary>
        [Range(100, 20_000)] public int DistanceCm { get; set; }

        /// <summary>Height above the character's feet the camera looks at.</summary>
        [Range(0, 1_000)] public int LookAtHeightCm { get; set; }

        /// <summary>Vertical field of view.</summary>
        [Range(10, 120)] public int FieldOfViewDegrees { get; set; }

        /// <summary>Turn speed while a camera turn key is held.</summary>
        [Range(1, 1_440)] public int RotateDegreesPerSec { get; set; }

        /// <summary>Time constant of the settle onto the nearest 45-degree step after the turn keys are released; 0 snaps.</summary>
        [Range(0, 5_000)] public int YawSettleMs { get; set; }

        public void Validate(RowErrors errors)
        {
            if (LookAtHeightCm >= DistanceCm)
                errors.Add(nameof(LookAtHeightCm), "must be less than DistanceCm");
        }
    }
}
