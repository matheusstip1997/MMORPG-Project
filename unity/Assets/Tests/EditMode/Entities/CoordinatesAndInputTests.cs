using Game.Gameplay.Entities;
using Game.Gameplay.Input;
using NUnit.Framework;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;
using UnityEngine;

namespace Game.Tests.EditMode.Entities
{
    /// <summary>Sim, snapshot and Unity axes (WorldUnits, SnapshotAxes) and the screen-relative input mapping.</summary>
    public class CoordinatesAndInputTests
    {
        [Test]
        public void Ground_Y_is_Unity_z_height_is_Unity_y_and_cm_become_metres()
        {
            Assert.AreEqual(new Vector3(1.5f, 2f, -3f), WorldUnits.FromGroundCm(150, -300, 200));
            Assert.AreEqual(new Vector3(1.5f, 2f, -3f), WorldUnits.FromSnapshotCm(150, 200, -300));
        }

        [Test]
        public void A_snapshot_entity_maps_back_to_the_simulation_ground_plane()
        {
            var entity = new EntitySnapshot { XCm = 150, YCm = 200, ZCm = -300, Facing = 16_384 };

            var state = SnapshotAxes.GroundState(entity);

            Assert.AreEqual(FixVec2.FromCm(150, -300), state.Position);
            Assert.AreEqual(16_384, state.Facing);
            Assert.AreEqual(200, SnapshotAxes.HeightCm(entity));
        }

        [Test]
        public void Moving_W_in_the_simulation_moves_toward_Unity_plus_z()
        {
            var after = MovementSim.Step(new MovementState(FixVec2.Zero, 0),
                new MovementInput(Shared.Simulation.MoveKeys.W, 0), false, 400, 20);

            var world = WorldUnits.FromGroundCm(after.Position.X / 256f, after.Position.Y / 256f, 0);

            Assert.AreEqual(0f, world.x, 1e-6f);
            Assert.Greater(world.z, 0f);
        }

        [TestCase(0, 0f)]
        [TestCase(16_384, 90f)]
        [TestCase(32_768, 180f)]
        [TestCase(49_152, 270f)]
        public void Facing_is_a_clockwise_yaw_from_plus_z(int facing, float yaw)
        {
            Assert.AreEqual(yaw, WorldUnits.FacingToYawDegrees(facing), 1e-4f);
            // Unity yaw rotates +z toward +x: facing east (90 degrees) points along +x.
            var forward = WorldUnits.FacingRotation(facing) * Vector3.forward;
            Assert.IsTrue(WorldUnits.TryFacingFromDirection(forward, out ushort back));
            Assert.AreEqual((ushort)facing, back);
        }

        [Test]
        public void Facing_from_a_vertical_or_zero_direction_is_refused()
        {
            Assert.IsFalse(WorldUnits.TryFacingFromDirection(Vector3.up, out _));
            Assert.IsFalse(WorldUnits.TryFacingFromDirection(Vector3.zero, out _));
        }

        [Test]
        public void Ground_aim_points_from_the_character_to_the_pointer_on_the_ground()
        {
            var cameraObject = new GameObject("TestCamera", typeof(Camera));
            try
            {
                var camera = cameraObject.GetComponent<Camera>();
                camera.transform.SetPositionAndRotation(new Vector3(0, 10, -10), Quaternion.Euler(45, 0, 0));
                // The character stands where the camera looks; aim at a point 5 m east of it on the ground.
                var character = new Vector3(0, 0, 0);
                var screen = camera.WorldToScreenPoint(new Vector3(5, 0, 0));

                Assert.IsTrue(GroundAim.TryFacing(camera, screen, character, out ushort facing));
                Assert.AreEqual(16_384, facing, 64);
            }
            finally
            {
                Object.DestroyImmediate(cameraObject);
            }
        }

        [Test]
        public void The_keys_are_offsets_from_the_mouse_whatever_the_camera_step()
        {
            // R-2.2-01, R-2.2-05 (M3 decision 14 as revised): the client sends the keys as held (no camera mapping any more),
            // so at every camera step and mouse direction W runs at the mouse, S away, D to its right and A to its left.
            for (int step = 0; step < CameraYaw.Steps; step++)
            {
                foreach (int offset in new[] { 0, 3_648, -3_648, 12_736 }) // screen-up, 20 degrees either side, 70 degrees
                {
                    ushort mouse = (ushort)(step * 8_192 + offset);
                    Assert.Less(Off(Moved(Shared.Protocol.MoveKeys.W, mouse), mouse), 0.1, $"W, step {step}, offset {offset}");
                    Assert.Less(Off(Moved(Shared.Protocol.MoveKeys.S, mouse), (ushort)(mouse + 32_768)), 0.1, $"S, step {step}, offset {offset}");
                    Assert.Less(Off(Moved(Shared.Protocol.MoveKeys.D, mouse), (ushort)(mouse + 16_384)), 0.1, $"D, step {step}, offset {offset}");
                    Assert.Less(Off(Moved(Shared.Protocol.MoveKeys.A, mouse), (ushort)(mouse - 16_384)), 0.1, $"A, step {step}, offset {offset}");
                    Assert.Less(Off(Moved(Shared.Protocol.MoveKeys.W | Shared.Protocol.MoveKeys.D, mouse), (ushort)(mouse + 8_192)), 0.1,
                        $"W+D, step {step}, offset {offset}");
                }
            }
        }

        private static FixVec2 Moved(Shared.Protocol.MoveKeys held, ushort facing)
        {
            var input = MovementInputMapping.FromFrame(new InputFrame { Keys = held, Facing = facing });
            return MovementSim.Step(new MovementState(FixVec2.Zero, 0), input, false, 400, 20).Position;
        }

        // Degrees between a ground move and a binary angle (clockwise from +Y).
        private static double Off(FixVec2 moved, ushort angle)
        {
            double degrees = System.Math.Atan2(moved.X, moved.Y) * 180 / System.Math.PI - angle * 360.0 / 65536;
            return System.Math.Abs(((degrees % 360) + 540) % 360 - 180);
        }

        [Test]
        public void W_moves_toward_the_mouse_on_screen_at_every_camera_step()
        {
            // With the mouse screen-up (the facing along the camera's forward), W runs toward the top of the screen.
            for (int step = 0; step < CameraYaw.Steps; step++)
            {
                var after = Moved(Shared.Protocol.MoveKeys.W, (ushort)(step * 8_192));
                var moved = WorldUnits.FromGroundCm(after.X, after.Y, 0).normalized;
                var screenUp = Quaternion.Euler(0, step * CameraYaw.StepDegrees, 0) * Vector3.forward;

                Assert.Greater(Vector3.Dot(moved, screenUp), 0.999f, $"camera step {step}");
            }
        }

        [Test]
        public void Camera_turns_while_held_and_settles_on_the_nearest_45_degree_step()
        {
            var yaw = new CameraYaw(new ClientCamera { RotateDegreesPerSec = 120, YawSettleMs = 100 });

            yaw.Update(+1, 250); // 30 degrees
            Assert.AreEqual(30f, yaw.Degrees, 1e-3f);
            Assert.AreEqual(1, yaw.Step);
            for (int i = 0; i < 100; i++)
                yaw.Update(0, 16);

            Assert.AreEqual(45f, yaw.Degrees, 1e-3f);
            yaw.Update(-1, 500); // back 60 degrees
            Assert.AreEqual(-15f, yaw.Degrees, 1e-3f);
            Assert.AreEqual(0, yaw.Step);
        }

        [Test]
        public void Camera_settle_of_zero_snaps_to_the_step()
        {
            var yaw = new CameraYaw(new ClientCamera { RotateDegreesPerSec = 90, YawSettleMs = 0 });
            yaw.Update(+1, 600); // 54 degrees

            yaw.Update(0, 1);

            Assert.AreEqual(45f, yaw.Degrees, 1e-6f);
        }

        [Test]
        public void Client_config_loads_and_validates_the_repository_tables()
        {
            Assert.IsTrue(ClientConfig.TryLoad(out var set, out string error), error);
            Assert.AreEqual(20, set.Single<StartingValuesM1>().TickRateHz);
            Assert.GreaterOrEqual(set.Single<ClientPrediction>().CorrectionToleranceCm, 2);
            Assert.Greater(set.Single<ClientCamera>().DistanceCm, 0);
        }
    }
}
