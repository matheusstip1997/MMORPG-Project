using Shared.Data;
using Shared.Data.Tables;

namespace Shared.Tests.Data;

/// <summary>R-2.2-07: the BodyCollisionSettings row rules.</summary>
public class BodyCollisionSettingsTests
{
    [Fact]
    public void Unknown_responses_are_refused()
    {
        var row = new BodyCollisionSettings { WalkResponse = (BodyResponse)7, DashResponse = (BodyResponse)9, MaxSlideIterations = 2 };
        var errors = new RowErrors();
        row.Validate(errors);
        Assert.Equal(new[] { nameof(BodyCollisionSettings.WalkResponse), nameof(BodyCollisionSettings.DashResponse) },
            errors.Items.Select(e => e.Field));
    }

    [Fact]
    public void Known_responses_pass()
    {
        var row = new BodyCollisionSettings { WalkResponse = BodyResponse.Stop, DashResponse = BodyResponse.Slide, MaxSlideIterations = 1 };
        var errors = new RowErrors();
        row.Validate(errors);
        Assert.Empty(errors.Items);
    }
}
