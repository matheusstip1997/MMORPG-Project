using Shared.Protocol;
using Shared.Simulation.Stats;

namespace Shared.Tests.Protocol;

/// <summary>The free attribute point messages (protocol 25, WP-M2-13, R-3.4-08): round trips and the checks of their readers.</summary>
public class AttributeMessagesTests
{
    [Fact]
    public void A_request_and_its_result_round_trip_through_the_guarded_reader()
    {
        var request = ReliableMessages.Write(ReliableMessageId.AttributeSpendRequest,
            new AttributeSpendRequest { RequestId = 7, Attribute = Attributes.Int });
        Assert.True(ReliableMessageReader.IsWellFormed(request, 7)); // the ReliableMessageCaps row
        Assert.True(ReliableMessages.TryRead(request, out AttributeSpendRequest readRequest));
        Assert.Equal((7u, (byte)Attributes.Int), (readRequest.RequestId, readRequest.Attribute));

        var result = ReliableMessages.Write(ReliableMessageId.AttributeSpendResult,
            new AttributeSpendResult { RequestId = 7, Attribute = Attributes.Int, Reason = AttributeReason.NoAttributePoints });
        Assert.True(ReliableMessageReader.IsWellFormed(result, int.MaxValue));
        Assert.True(ReliableMessages.TryRead(result, out AttributeSpendResult readResult));
        Assert.Equal((7u, (byte)Attributes.Int, AttributeReason.NoAttributePoints), (readResult.RequestId, readResult.Attribute, readResult.Reason));
    }

    [Fact]
    public void An_unknown_attribute_or_reason_is_refused_by_the_reader()
    {
        var request = ReliableMessages.Write(ReliableMessageId.AttributeSpendRequest,
            new AttributeSpendRequest { RequestId = 1, Attribute = Attributes.Count });
        Assert.False(ReliableMessages.TryRead(request, out AttributeSpendRequest _));

        var result = ReliableMessages.Write(ReliableMessageId.AttributeSpendResult,
            new AttributeSpendResult { RequestId = 1, Reason = ReliableMessages.LastAttributeReason + 1 });
        Assert.False(ReliableMessages.TryRead(result, out AttributeSpendResult _));
    }

    [Fact]
    public void The_progression_state_carries_the_unspent_and_placed_points()
    {
        var bytes = ReliableMessages.Write(ReliableMessageId.ProgressionState, new ProgressionState
        {
            Level = 5, Cause = ProgressionCause.AttributePoints, AttributePoints = 2, StrPlaced = 1, DexPlaced = 0, StaPlaced = 1, IntPlaced = 0,
        });
        Assert.True(ReliableMessages.TryRead(bytes, out ProgressionState read));
        Assert.Equal((ProgressionCause.AttributePoints, 2), (read.Cause, read.AttributePoints));
        Assert.Equal((1, 0, 1, 0), (read.Placed(Attributes.Str), read.Placed(Attributes.Dex), read.Placed(Attributes.Sta), read.Placed(Attributes.Int)));

        var negative = ReliableMessages.Write(ReliableMessageId.ProgressionState, new ProgressionState { Level = 5, StaPlaced = -1 });
        Assert.False(ReliableMessages.TryRead(negative, out ProgressionState _));
    }
}
