using Shared.Util;

namespace Shared.Tests.Util;

public class Pcg32RngTests
{
    [Fact]
    public void Matches_the_reference_pcg32_demo_output()
    {
        // pcg32-demo from pcg-random.org: pcg32_srandom_r(&rng, 42u, 54u), first six outputs.
        var rng = new Pcg32Rng(42, 54);

        uint[] expected = { 0xa15c02b7, 0x7b47f409, 0xba1d3330, 0x83d2f293, 0xbfa4784b, 0xcbed606e };
        foreach (var value in expected)
            Assert.Equal(value, rng.NextUInt());
    }

    [Fact]
    public void Same_seed_and_stream_give_the_same_sequence()
    {
        var a = new Pcg32Rng(123456789, 3);
        var b = new Pcg32Rng(123456789, 3);

        for (int i = 0; i < 10_000; i++)
            Assert.Equal(a.NextUInt(), b.NextUInt());
    }

    [Fact]
    public void Different_streams_give_different_sequences()
    {
        var a = new Pcg32Rng(123456789, 3);
        var b = new Pcg32Rng(123456789, 4);

        Assert.NotEqual(Draw(a, 8), Draw(b, 8));
    }

    [Fact]
    public void Exposes_seed_and_stream_for_logging()
    {
        var rng = new Pcg32Rng(99, 7);

        Assert.Equal(99UL, rng.Seed);
        Assert.Equal(7UL, rng.Stream);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(100)]
    [InlineData(int.MaxValue)]
    public void NextInt_stays_below_the_bound(int maxExclusive)
    {
        var rng = new Pcg32Rng(5);

        for (int i = 0; i < 5_000; i++)
        {
            int value = rng.NextInt(maxExclusive);
            Assert.InRange(value, 0, maxExclusive - 1);
        }
    }

    [Theory]
    [InlineData(-10, 10)]
    [InlineData(int.MinValue, int.MaxValue)]
    [InlineData(int.MinValue, int.MinValue + 1)]
    [InlineData(int.MaxValue - 1, int.MaxValue)]
    public void NextInt_range_stays_inside_the_range(int minInclusive, int maxExclusive)
    {
        var rng = new Pcg32Rng(6);

        for (int i = 0; i < 5_000; i++)
        {
            int value = rng.NextInt(minInclusive, maxExclusive);
            Assert.InRange(value, minInclusive, maxExclusive - 1);
        }
    }

    [Fact]
    public void NextInt_covers_every_value_of_a_small_range()
    {
        var rng = new Pcg32Rng(8);
        var seen = new bool[6];

        for (int i = 0; i < 1_000; i++)
            seen[rng.NextInt(6)] = true;

        Assert.All(seen, Assert.True);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NextInt_rejects_a_non_positive_bound(int maxExclusive)
    {
        var rng = new Pcg32Rng(1);

        Assert.Throws<ArgumentOutOfRangeException>(() => rng.NextInt(maxExclusive));
    }

    [Fact]
    public void NextInt_rejects_an_empty_range()
    {
        var rng = new Pcg32Rng(1);

        Assert.Throws<ArgumentOutOfRangeException>(() => rng.NextInt(5, 5));
    }

    [Fact]
    public void RollPercent_never_succeeds_at_zero_and_always_at_one_hundred()
    {
        var rng = new Pcg32Rng(9);

        for (int i = 0; i < 2_000; i++)
        {
            Assert.False(rng.RollPercent(0));
            Assert.False(rng.RollPercent(-5));
            Assert.True(rng.RollPercent(100));
            Assert.True(rng.RollPercent(150));
        }
    }

    [Fact]
    public void RollPercent_consumes_one_draw_whatever_the_chance()
    {
        var a = new Pcg32Rng(10);
        var b = new Pcg32Rng(10);

        a.RollPercent(0);
        b.RollPercent(100);

        Assert.Equal(a.NextUInt(), b.NextUInt());
    }

    [Fact]
    public void RollPercent_is_close_to_the_requested_chance()
    {
        var rng = new Pcg32Rng(11);
        int hits = 0;

        for (int i = 0; i < 100_000; i++)
            if (rng.RollPercent(25)) hits++;

        Assert.InRange(hits, 24_000, 26_000);
    }

    private static uint[] Draw(IRng rng, int count)
    {
        var values = new uint[count];
        for (int i = 0; i < count; i++) values[i] = rng.NextUInt();
        return values;
    }
}
