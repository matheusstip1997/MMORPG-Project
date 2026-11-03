using GameServer.Net;
using static GameServer.Tests.Support.TestWorlds;

namespace GameServer.Tests.Net;

public class InputQueueTests
{
    [Fact]
    public void Frames_come_out_in_the_order_they_went_in()
    {
        var queue = new InputQueue(3);
        queue.Enqueue(Frame(1));
        queue.Enqueue(Frame(2));

        Assert.True(queue.TryDequeue(out var a));
        queue.Enqueue(Frame(3));
        queue.Enqueue(Frame(4));
        Assert.True(queue.TryDequeue(out var b));
        Assert.True(queue.TryDequeue(out var c));
        Assert.True(queue.TryDequeue(out var d));

        Assert.Equal([1, 2, 3, 4], new[] { a.Sequence, b.Sequence, c.Sequence, d.Sequence }.Select(s => (int)s));
        Assert.False(queue.TryDequeue(out _));
    }

    [Fact]
    public void A_full_queue_drops_the_oldest_frame_and_counts_it()
    {
        var queue = new InputQueue(2);
        for (ushort s = 1; s <= 5; s++)
            queue.Enqueue(Frame(s));

        Assert.Equal(2, queue.Count);
        Assert.Equal(3, queue.Dropped);
        Assert.True(queue.TryDequeue(out var oldest));
        Assert.Equal(4, oldest.Sequence);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(1)] // the dropped frame's buttons go into the frame that replaces it
    public void A_dropped_frame_keeps_its_buttons_in_the_next_frame(int capacity)
    {
        // Spec 1.6 (fix/parry-feel): a full queue drops the oldest frame's movement, never a press it carried.
        var queue = new InputQueue(capacity);
        queue.Enqueue(Frame(1, Shared.Protocol.MoveKeys.W, actions: Shared.Protocol.InputActions.Parry, skillSlot: 3));
        for (ushort s = 2; s <= capacity + 1; s++)
            queue.Enqueue(Frame(s, Shared.Protocol.MoveKeys.D));

        Assert.Equal(1, queue.Dropped);
        Assert.True(queue.TryDequeue(out var next));
        Assert.Equal((2, Shared.Protocol.InputActions.Parry, (byte)3, Shared.Protocol.MoveKeys.D),
            (next.Sequence, next.Actions, next.SkillSlot, next.Keys));
    }
}
