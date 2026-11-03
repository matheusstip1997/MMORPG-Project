#nullable enable
using Game.Gameplay.Input;
using Shared.Data.Tables;
using UnityEngine;

namespace Game.Gameplay.Entities
{
    /// <summary>
    /// The top-down camera (spec 1.1, R-2.2-05): fixed 45-degree pitch, centred on the character, turned only
    /// horizontally by the player (<see cref="CameraYaw"/>). Distance, look-at height and field of view come from the
    /// <see cref="ClientCamera"/> table. Follows the drawn (smoothed) position, so corrections never shake it.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public sealed class TopDownCameraRig : MonoBehaviour
    {
        /// <summary>Spec 1.1: the pitch is part of the game's look, not a setting.</summary>
        public const float PitchDegrees = 45f;

        private ClientCamera? _settings;
        private CameraYaw? _yaw;
        private Camera? _camera;

        public Camera Camera => _camera != null ? _camera : _camera = GetComponent<Camera>();

        /// <summary>The 45-degree step the movement keys are rotated by (<see cref="ScreenRelativeKeys"/>).</summary>
        public int YawStep => _yaw?.Step ?? 0;

        public float YawDegrees => _yaw?.Degrees ?? 0f;

        public void Configure(ClientCamera settings)
        {
            _settings = settings;
            _yaw = new CameraYaw(settings);
            Camera.fieldOfView = settings.FieldOfViewDegrees;
        }

        /// <summary>Turns the camera; call once per frame with the held turn direction.</summary>
        public void Turn(int direction, float deltaMs) => _yaw?.Update(direction, deltaMs);

        /// <summary>Places the camera around <paramref name="target"/> (Unity world position of the character's feet).</summary>
        public void Follow(Vector3 target)
        {
            if (_settings == null || _yaw == null)
                return;
            var rotation = Quaternion.Euler(PitchDegrees, _yaw.Degrees, 0f);
            var lookAt = target + Vector3.up * (_settings.LookAtHeightCm / WorldUnits.CmPerUnit);
            transform.SetPositionAndRotation(lookAt - rotation * Vector3.forward * (_settings.DistanceCm / WorldUnits.CmPerUnit), rotation);
        }
    }
}
