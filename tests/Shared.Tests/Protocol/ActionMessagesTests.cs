using Shared.Protocol;

namespace Shared.Tests.Protocol;

/// <summary>The reliable refusal message of backlog D2 (spec 1.7, 9.7).</summary>
public class ActionMessagesTests
{
    [Fact]
    public void ActionResult_round_trips()
    {
        var buffer = new byte[ActionResult.Size];
        Assert.Equal(ActionResult.Size, ActionResult.Write(buffer, 65_000, 4095, 11, ActionReason.SkillNotLearned));
        Assert.Equal((byte)ReliableMessageId.ActionResult, buffer[0]);
        Assert.True(ActionResult.TryRead(buffer, out ushort sequence, out ushort actionId, out byte slot, out ActionReason reason));
        Assert.Equal((65_000, 4095, 11, ActionReason.SkillNotLearned), (sequence, actionId, slot, reason));
    }

    [Fact]
    public void Malformed_results_are_refused()
    {
        var buffer = new byte[ActionResult.Size];
        ActionResult.Write(buffer, 1, 2, 0, ActionReason.OnCooldown);
        Assert.False(ActionResult.TryRead(buffer.AsSpan(0, ActionResult.Size - 1), out _, out _, out _, out _));
        Assert.False(ActionResult.TryRead([.. buffer, 0], out _, out _, out _, out _));
        byte[] wrongId = [.. buffer];
        wrongId[0] = 9;
        Assert.False(ActionResult.TryRead(wrongId, out _, out _, out _, out _));
        foreach (byte code in new byte[] { 0, (byte)ActionResult.LastReason + 1, 255 })
        {
            byte[] badReason = [.. buffer];
            badReason[6] = code;
            Assert.False(ActionResult.TryRead(badReason, out _, out _, out _, out _));
        }
        Assert.Throws<ArgumentException>(() => ActionResult.Write(new byte[ActionResult.Size - 1], 1, 2, 0, ActionReason.OnCooldown));
    }

    [Fact]
    public void Reason_codes_are_stable()
    {
        // Append only: the client maps these numbers to texts (ReasonCodeStrings).
        Assert.Equal(1, (int)ActionReason.OnCooldown);
        Assert.Equal(2, (int)ActionReason.SkillNotLearned);
        Assert.Equal(3, (int)ActionReason.NoStamina);
        Assert.Equal(4, (int)ActionReason.DodgeNoCharge);
        Assert.Equal(5, (int)ActionReason.ParryNoCharge);
        Assert.Equal(ActionReason.ParryNoCharge, ActionResult.LastReason);
    }
}
