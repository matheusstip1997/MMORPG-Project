using Shared.Data;
using Shared.Data.Tables;

namespace Shared.Tests.Data;

public class ClientTablesTests
{
    private static ClientPrediction Prediction(int toleranceCm, int snapCm) => new()
    {
        CorrectionToleranceCm = toleranceCm,
        CorrectionSnapCm = snapCm,
    };

    [Fact]
    public void Prediction_tolerance_must_be_below_the_snap_distance()
    {
        var errors = new RowErrors();

        Prediction(toleranceCm: 300, snapCm: 300).Validate(errors);

        Assert.Equal(nameof(ClientPrediction.CorrectionToleranceCm), Assert.Single(errors.Items).Field);
    }

    [Fact]
    public void Prediction_tolerance_below_the_snap_distance_is_valid()
    {
        var errors = new RowErrors();

        Prediction(toleranceCm: 2, snapCm: 300).Validate(errors);

        Assert.Empty(errors.Items);
    }

    [Fact]
    public void Prediction_tolerance_of_one_cm_is_out_of_range_because_snapshot_rounding_alone_reaches_it()
    {
        var row = new ClientPrediction
        {
            CorrectionToleranceCm = 1, CorrectionSnapCm = 300, CorrectionSmoothingMs = 100, MaxPendingInputs = 64,
            MaxTicksPerFrame = 4, InterpolationClockCatchUpMs = 500,
        };
        var set = new ConfigSet();
        set.Add(new ConfigTable(typeof(ClientPrediction), 1, new object[] { row }));

        var error = Assert.Single(TableValidator.Validate(set));

        Assert.Equal(ValidationCodes.OutOfRange, error.Code);
        Assert.Equal(nameof(ClientPrediction.CorrectionToleranceCm), error.Field);
    }

    [Fact]
    public void Camera_look_at_height_must_be_below_the_distance()
    {
        var errors = new RowErrors();

        new ClientCamera { DistanceCm = 500, LookAtHeightCm = 500 }.Validate(errors);

        Assert.Equal(nameof(ClientCamera.LookAtHeightCm), Assert.Single(errors.Items).Field);
    }
}
