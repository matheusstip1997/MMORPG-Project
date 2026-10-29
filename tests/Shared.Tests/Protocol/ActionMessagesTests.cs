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
        Assert.Equal(6, (int)ActionReason.ParrySuccess);
        Assert.Equal(7, (int)ActionReason.ParryReduced);
        Assert.Equal(8, (int)ActionReason.ParryFailedRed);
        Assert.Equal(9, (int)ActionReason.ParryFailedArmor);
        Assert.Equal(10, (int)ActionReason.ParryFailedTiming);
        Assert.Equal(11, (int)ActionReason.NoMp); // R-3.1-04, backlog E (protocol 7)
        Assert.Equal(12, (int)ActionReason.ParryFailedEarly); // protocol 15
        Assert.Equal(13, (int)ActionReason.ParryFailedLate);
        Assert.Equal(14, (int)ActionReason.WrongWeapon); // R-3.5-07, WP-M2-5 (protocol 20)
        Assert.Equal(ActionReason.WrongWeapon, ActionResult.LastReason);
    }

    [Fact]
    public void Parry_results_round_trip_and_are_told_apart_from_refusals()
    {
        // Spec 9.7: the parry result text comes from an ActionResult reason (WP6).
        var buffer = new byte[ActionResult.Size];
        foreach (var reason in new[]
                 {
                     ActionReason.ParrySuccess, ActionReason.ParryReduced, ActionReason.ParryFailedRed, ActionReason.ParryFailedArmor,
                     ActionReason.ParryFailedTiming, ActionReason.ParryFailedEarly, ActionReason.ParryFailedLate,
                 })
        {
            ActionResult.Write(buffer, 42, 3, 0, reason);
            Assert.True(ActionResult.TryRead(buffer, out ushort sequence, out ushort actionId, out byte slot, out var read));
            Assert.Equal((42, 3, 0, reason), (sequence, actionId, slot, read));
            Assert.True(ActionResult.IsParryResult(reason));
        }
        Assert.False(ActionResult.IsParryResult(ActionReason.OnCooldown));
        Assert.False(ActionResult.IsParryResult(ActionReason.ParryNoCharge)); // also a refusal
        Assert.False(ActionResult.IsParryResult(ActionReason.NoMp));
        buffer[6] = (byte)ActionResult.LastReason + 1;
        Assert.False(ActionResult.TryRead(buffer, out _, out _, out _, out _));
    }
}
