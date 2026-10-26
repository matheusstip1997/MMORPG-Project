using BotClient.Link;
using Shared.Util;

namespace GameServer.Tests.Bots;

/// <summary>Backlog J link simulation: one direction's loss and delivery times, pure and seeded (architecture rule 6).</summary>
public class LinkConditionerTests
{
    [Fact]
    public void Without_jitter_every_datagram_is_due_after_the_fixed_delay()
    {
        var link = new LinkConditioner(delayMs: 50, jitterMs: 0, lossPercent: 0, allowReorder: false, new Pcg32Rng(1));

        for (long now = 0; now < 10_000_000; now += 33_333)
        {
            Assert.True(link.Admit(now, out long due));
            Assert.Equal(now + 50_000, due);
        }
        Assert.Equal(0, link.Dropped);
    }

    [Fact]
    public void Jitter_stays_within_its_bounds_and_never_goes_below_zero()
    {
        var link = new LinkConditioner(delayMs: 100, jitterMs: 10, lossPercent: 0, allowReorder: true, new Pcg32Rng(2));
        var delays = new List<long>();
        for (int i = 0; i < 10_000; i++)
        {
            long now = i * 1_000L;
            Assert.True(link.Admit(now, out long due));
            delays.Add(due - now);
        }
        Assert.All(delays, d => Assert.InRange(d, 90_000, 110_000));
        Assert.True(delays.Distinct().Count() > 100, "jitter should spread the delays");

        var small = new LinkConditioner(delayMs: 2, jitterMs: 10, lossPercent: 0, allowReorder: true, new Pcg32Rng(3));
        for (int i = 0; i < 1_000; i++)
        {
            Assert.True(small.Admit(i * 100L, out long due));
            Assert.True(due >= i * 100L);
        }
    }

    [Fact]
    public void Without_reordering_jitter_never_lets_a_datagram_overtake_an_earlier_one()
    {
        var ordered = new LinkConditioner(delayMs: 50, jitterMs: 20, lossPercent: 0, allowReorder: false, new Pcg32Rng(4));
        var reordering = new LinkConditioner(delayMs: 50, jitterMs: 20, lossPercent: 0, allowReorder: true, new Pcg32Rng(4));
        long lastOrdered = long.MinValue;
        int overtaken = 0;
        long lastReordering = long.MinValue;
        for (int i = 0; i < 5_000; i++)
        {
            long now = i * 1_000L; // 1 ms apart, far less than the jitter
            Assert.True(ordered.Admit(now, out long due));
            Assert.True(due >= lastOrdered);
            Assert.True(due >= now + 30_000);
            lastOrdered = due;

            Assert.True(reordering.Admit(now, out long freeDue));
            if (freeDue < lastReordering)
                overtaken++;
            lastReordering = freeDue;
        }
        Assert.True(overtaken > 0, "with reordering allowed some datagrams overtake earlier ones");
    }

    [Fact]
    public void Losses_follow_the_seed_exactly_and_the_rate_over_many_datagrams()
    {
        static List<bool> Run(ulong seed)
        {
            var link = new LinkConditioner(delayMs: 100, jitterMs: 10, lossPercent: 2, allowReorder: false, new Pcg32Rng(seed, 7));
            var kept = new List<bool>();
            for (int i = 0; i < 10_000; i++)
                kept.Add(link.Admit(i * 50_000L, out _));
            return kept;
        }

        var first = Run(42);
        Assert.Equal(first, Run(42)); // same seed and stream: the same datagrams are lost
        Assert.NotEqual(first, Run(43));
        int lost = first.Count(k => !k);
        Assert.InRange(lost, 120, 280); // 2 % of 10 000 is 200
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(100, 10_000)]
    public void Zero_and_full_loss_are_exact(int lossPercent, int expectedLost)
    {
        var link = new LinkConditioner(delayMs: 0, jitterMs: 0, lossPercent, allowReorder: false, new Pcg32Rng(5));
        for (int i = 0; i < 10_000; i++)
            link.Admit(i, out _);
        Assert.Equal(expectedLost, link.Dropped);
        Assert.Equal(10_000 - expectedLost, link.Admitted);
    }

    [Fact]
    public void Profiles_set_each_direction()
    {
        var profile = new Shared.Data.Tables.BotLinkProfile { Id = "x", UpDelayMs = 30, DownDelayMs = 70 };

        Assert.True(LinkConditioner.Up(profile, new Pcg32Rng(1)).Admit(0, out long up));
        Assert.True(LinkConditioner.Down(profile, new Pcg32Rng(1)).Admit(0, out long down));

        Assert.Equal((30_000L, 70_000L), (up, down));
    }
}

/// <summary>Backlog J: the relay's hold queue.</summary>
public class DelayQueueTests
{
    [Fact]
    public void Datagrams_leave_by_due_time_and_ties_keep_arrival_order()
    {
        var queue = new DelayQueue<string>();
        queue.Enqueue(300, [3], "c");
        queue.Enqueue(100, [1], "a");
        queue.Enqueue(200, [2], "b1");
        queue.Enqueue(200, [22, 22], "b2");

        Assert.True(queue.TryPeekDue(out long first));
        Assert.Equal(100, first);
        Assert.False(queue.TryDequeueDue(99, out _));

        var order = new List<(string, byte[])>();
        while (queue.TryDequeueDue(1_000, out var datagram))
        {
            order.Add((datagram.Target, datagram.Data.ToArray()));
            DelayQueue<string>.Return(datagram);
        }
        Assert.Equal(["a", "b1", "b2", "c"], order.Select(o => o.Item1));
        Assert.Equal(new byte[] { 22, 22 }, order[2].Item2);
        Assert.Equal(0, queue.Count);
    }
}
