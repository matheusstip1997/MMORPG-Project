using Shared.Data.Tables;
using Shared.Protocol;

namespace Shared.Tests.Protocol;

/// <summary>
/// The NPC interaction request and its answer (spec 4.2, WP-M2-4): every <see cref="NpcInteraction"/> kind reads, so a kind added
/// later is not dropped as malformed by a bound left behind (WP-M4-10 part B: the contract board's kind 10 was refused while
/// <see cref="OutpostMessages.LastInteraction"/> was still the auction), and a kind above the last is refused.
/// </summary>
public class OutpostMessagesTests
{
    [Fact]
    public void The_last_interaction_is_the_highest_kind()
    {
        Assert.Equal(Enum.GetValues<NpcInteraction>().Max(), OutpostMessages.LastInteraction);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Every_interaction_kind_reads_as_a_request_and_an_answer(NpcInteraction kind)
    {
        var request = ReliableMessages.Write(ReliableMessageId.OutpostInteractRequest,
            new OutpostInteractRequest { RequestId = 3, NpcEntityId = 11, Kind = kind });
        Assert.True(ReliableMessages.TryRead(request, out OutpostInteractRequest read));
        Assert.Equal(kind, read.Kind);
        var answer = ReliableMessages.Write(ReliableMessageId.OutpostInteractResult,
            new OutpostInteractResult { RequestId = 3, NpcEntityId = 11, Kind = kind });
        Assert.True(ReliableMessages.TryRead(answer, out OutpostInteractResult result));
        Assert.Equal(kind, result.Kind);
    }

    [Fact]
    public void A_kind_above_the_last_is_refused()
    {
        var request = ReliableMessages.Write(ReliableMessageId.OutpostInteractRequest,
            new OutpostInteractRequest { RequestId = 3, NpcEntityId = 11, Kind = OutpostMessages.LastInteraction + 1 });
        Assert.False(ReliableMessages.TryRead(request, out OutpostInteractRequest _));
    }

    public static TheoryData<NpcInteraction> Kinds()
    {
        var data = new TheoryData<NpcInteraction>();
        foreach (var kind in Enum.GetValues<NpcInteraction>())
            data.Add(kind);
        return data;
    }
}
