using Game.Gameplay.Prediction;
using GameServer.Host.Config;
using GameServer.Tests.Support;
using Shared.Data;
using Shared.Protocol;
using Shared.Simulation;
using Shared.World;
using MoveKeys = Shared.Protocol.MoveKeys;

namespace GameServer.Tests.World;

/// <summary>
/// The client's prediction of the tutorial gate (R-2.3-06, R-12.3-01; WP-M2-10): <c>LocalPlayerPredictor</c> (Unity client and
/// bots, ClientCore) stops where the server stops a player of its level and reports the level the gate needs, which the HUD
/// shows as the <c>RegionLevelTooLow</c> prompt.
/// </summary>
public class PredictedGateTests
{
    private const int BoundaryRaw = 1_000 * FixedPoint.RawPerCm;

    private static LocalPlayerPredictor Predictor(int level)
    {
        var tables = ConfigLoader.LoadAndValidate(RepositoryPaths.Tables(), ConfigTables.All).Set;
        var grid = TestMaps.Grid(0, 0, 100, 20, 10, baseHeightCm: 0, walkable: (_, _) => true,
            region: (x, _) => (byte)(x >= 10 ? 2 : 1));
        var predictor = new LocalPlayerPredictor(PredictionSettings.From(tables), grid);
        predictor.UseGates(RegionGates.FromMinLevels(new[] { 0, 0, 10 }));
        predictor.SetLevel(level);
        predictor.Reconcile(new SnapshotHeader { ServerTick = 1 }, new MovementState(FixVec2.FromCm(850, 450), 0));
        return predictor;
    }

    [Fact]
    public void Below_level_10_the_predicted_walk_stops_at_the_gate_and_names_the_level_it_needs()
    {
        var predictor = Predictor(level: 9);

        for (int i = 0; i < 20; i++)
            predictor.Predict(MoveKeys.D, 16384);

        Assert.Equal(BoundaryRaw - 1, predictor.State.Position.X);
        Assert.Equal(10, predictor.GateStopLevel);

        predictor.Predict(MoveKeys.A, 16384); // walking away: no refusal
        Assert.Equal(0, predictor.GateStopLevel);
    }

    [Fact]
    public void At_level_10_the_predicted_walk_goes_through()
    {
        var predictor = Predictor(level: 10);

        for (int i = 0; i < 20; i++)
            predictor.Predict(MoveKeys.D, 16384);

        Assert.True(predictor.State.Position.X > BoundaryRaw);
        Assert.Equal(0, predictor.GateStopLevel);
    }

    [Fact]
    public void Without_gates_the_prediction_is_unchanged()
    {
        var predictor = Predictor(level: 1);
        predictor.UseGates(null);

        for (int i = 0; i < 20; i++)
            predictor.Predict(MoveKeys.D, 16384);

        Assert.True(predictor.State.Position.X > BoundaryRaw);
        Assert.Equal(0, predictor.GateStopLevel);
    }
}
