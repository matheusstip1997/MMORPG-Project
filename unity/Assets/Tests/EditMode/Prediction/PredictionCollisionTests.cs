using Game.Gameplay.Prediction;
using NUnit.Framework;
using Shared.Data.Tables;
using Shared.Simulation;
using Shared.World;
using MoveKeys = Shared.Protocol.MoveKeys;

namespace Game.Tests.EditMode.Prediction
{
    /// <summary>
    /// Client prediction with the world grid (spec 2.2, backlog C3/D1): the fake server and the predictor collide with
    /// the same walls, so walking into one costs no corrections.
    /// </summary>
    public class PredictionCollisionTests
    {
        private const int Max = PredictionTestKit.GridMinCm + PredictionTestKit.GridSizeCm;
        private static readonly WorldGrid VerticalWall = PredictionTestKit.Grid(wall: (500, PredictionTestKit.GridMinCm, 700, Max));
        private static readonly WorldGrid HorizontalWall = PredictionTestKit.Grid(wall: (PredictionTestKit.GridMinCm, 500, Max, 700));

        private static PredictionSettings Settings => PredictionTestKit.Settings();

        // Raw Q24.8 stops: one raw unit before the wall's low edge (500 cm * 256 - 1), or exactly on its high edge (700 cm * 256).
        [TestCase(MoveKeys.D, 100, -50, true, 127_999)]
        [TestCase(MoveKeys.A, 1_100, -50, true, 179_200)]
        [TestCase(MoveKeys.W, 100, -50, false, 127_999)]
        [TestCase(MoveKeys.S, 100, 1_100, false, 179_200)]
        public void Walking_into_a_wall_with_latency_never_corrects(MoveKeys keys, int startXCm, int startYCm, bool vertical, int stopRaw)
        {
            var rig = new PredictionRig(Settings, delayTicks: 3, spawn: new MovementState(FixVec2.FromCm(startXCm, startYCm), 0),
                grid: vertical ? VerticalWall : HorizontalWall);

            rig.Run(60, keys);
            rig.Settle();

            Assert.AreEqual(0, rig.Corrections, rig.Describe());
            Assert.AreEqual(rig.Server.State, rig.Predictor.State);
            var position = rig.Server.State.Position;
            Assert.AreEqual(stopRaw, vertical ? position.X : position.Y, "the server stopped at the wall, not in or past it");
        }

        [Test]
        public void Pushing_diagonally_into_a_wall_slides_along_it_on_both_sides()
        {
            var rig = new PredictionRig(Settings, delayTicks: 3, grid: VerticalWall);

            rig.Run(80, MoveKeys.W | MoveKeys.D, facing: 8_192);
            rig.Settle();

            Assert.AreEqual(0, rig.Corrections, rig.Describe());
            Assert.AreEqual(rig.Server.State, rig.Predictor.State);
            Assert.AreEqual(127_999, rig.Server.State.Position.X);
            Assert.Greater(FixedPoint.ToCmRound(rig.Server.State.Position.Y), 800, "the free axis kept moving");
        }

        [Test]
        public void Walking_into_and_round_a_monster_body_with_latency_never_corrects()
        {
            // R-2.2-07: a still demon (50 cm) in the way of a 40 cm player, 30 cm off its line: both sides slide round it.
            var bodySettings = new BodyCollisionSettings
            {
                WalkResponse = BodyResponse.Slide, DashResponse = BodyResponse.Stop, OverlapToleranceCm = 2, MaxSlideIterations = 2,
            };
            var settings = PredictionTestKit.Settings(bodies: bodySettings);
            var rig = new PredictionRig(settings, delayTicks: 3, spawn: new MovementState(FixVec2.FromCm(0, 0), 0));
            var demon = FixVec2.FromCm(30, 300);
            rig.Server.Bodies.Add(7, demon, FixedPoint.FromCm(50));
            rig.Server.BodyRadiusRaw = FixedPoint.FromCm(40);
            rig.Server.BodyRules = settings.Bodies;
            rig.Predictor.UseBodies(new PredictionTestKit.StaticBodies(rig.Server.Bodies));
            rig.Predictor.OwnBodyRadiusRaw = FixedPoint.FromCm(40);
            long contact = FixedPoint.FromCm(88);
            bool touched = false;

            for (int i = 0; i < 50; i++)
            {
                rig.Tick(MoveKeys.W, 0);
                var p = rig.Server.State.Position;
                long dx = p.X - demon.X, dy = p.Y - demon.Y;
                Assert.GreaterOrEqual(dx * dx + dy * dy, contact * contact, "the server entered the body");
                touched |= dx * dx + dy * dy <= (contact + FixedPoint.FromCm(1)) * (contact + FixedPoint.FromCm(1));
            }
            rig.Settle();

            Assert.IsTrue(touched, "the walk never met the body");
            Assert.AreEqual(0, rig.Corrections, rig.Describe());
            Assert.AreEqual(rig.Server.State, rig.Predictor.State);
            Assert.Greater(rig.Server.State.Position.Y, demon.Y, "got round the body");
        }

        [Test]
        public void A_correction_while_pushing_into_a_wall_is_corrected_once()
        {
            // The snapshot rounds the stop just before the wall (500 cm - 1 raw) to 500 cm, on the wall cell; a replay
            // from there would walk into the wall and correct again on every snapshot.
            var rig = new PredictionRig(Settings, delayTicks: 3, grid: VerticalWall);
            rig.Run(40, MoveKeys.D);

            rig.Server.State = new MovementState(rig.Server.State.Position + FixVec2.FromCm(0, 30), rig.Server.State.Facing);
            rig.Run(40, MoveKeys.D);
            rig.Settle();

            Assert.AreEqual(1, rig.Corrections, rig.Describe());
            Assert.AreEqual(rig.Server.State, rig.Predictor.State);
        }

        [Test]
        public void The_predicted_height_is_the_grid_height_under_the_predicted_position()
        {
            var grid = PredictionTestKit.Grid(raised: (300, PredictionTestKit.GridMinCm, Max, Max), raisedCm: 120);
            var rig = new PredictionRig(Settings, delayTicks: 3, grid: grid);
            Assert.AreEqual(0, rig.Predictor.HeightCm);

            rig.Run(5, MoveKeys.D);
            Assert.AreEqual(0, rig.Predictor.HeightCm, "x = 200 cm is still on the ground");

            rig.Run(10, MoveKeys.D);
            Assert.AreEqual(120, rig.Predictor.HeightCm, "x = 400 cm is on the raised cells");
            rig.Settle();
            Assert.AreEqual(rig.Server.HeightCm, rig.Predictor.HeightCm);
        }
    }
}
