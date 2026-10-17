using GameServer.Net.Security;

namespace GameServer.Tests.Net.Security;

public class ReplayWindowTests
{
    private static bool Accept(ref ReplayWindow window, ulong sequence)
    {
        if (!window.CanAccept(sequence))
            return false;
        window.Mark(sequence);
        return true;
    }

    [Fact]
    public void Sequence_zero_is_never_valid()
    {
        var window = new ReplayWindow();

        Assert.False(window.CanAccept(0));
    }

    [Fact]
    public void Each_sequence_is_accepted_once()
    {
        var window = new ReplayWindow();

        Assert.True(Accept(ref window, 1));
        Assert.False(Accept(ref window, 1));
        Assert.True(Accept(ref window, 2));
        Assert.False(Accept(ref window, 2));
    }

    [Fact]
    public void Late_packets_inside_the_window_are_accepted_once()
    {
        var window = new ReplayWindow();
        Assert.True(Accept(ref window, 100));

        Assert.True(Accept(ref window, 100 - ReplayWindow.Size + 1));
        Assert.False(Accept(ref window, 100 - ReplayWindow.Size + 1));
        Assert.False(Accept(ref window, 100 - ReplayWindow.Size));
    }

    [Fact]
    public void A_large_jump_forgets_the_old_bitmap_without_accepting_old_sequences()
    {
        var window = new ReplayWindow();
        Assert.True(Accept(ref window, 5));

        Assert.True(Accept(ref window, 5 + 10_000));
        Assert.False(Accept(ref window, 5));
        Assert.True(Accept(ref window, 5 + 10_000 - 1));
    }

    [Fact]
    public void Marking_a_sequence_outside_the_window_does_not_corrupt_it()
    {
        var window = new ReplayWindow();
        Assert.True(Accept(ref window, 200));

        // C# masks shift counts, so an unguarded mark of 200 - 64 would set the bit for 200.
        window.Mark(200 - ReplayWindow.Size);

        Assert.True(Accept(ref window, 199));
        Assert.False(Accept(ref window, 200));
    }

    [Fact]
    public void Matches_a_reference_model_on_random_sequences()
    {
        var rng = new Random(2_5_02);
        var window = new ReplayWindow();
        var accepted = new HashSet<ulong>();
        ulong highest = 0;

        for (var i = 0; i < 20_000; i++)
        {
            var sequence = (ulong)Math.Max(0, (long)highest + rng.Next(-80, 20));
            var expected = sequence != 0 && !accepted.Contains(sequence) && (sequence > highest || highest - sequence < ReplayWindow.Size);

            Assert.Equal(expected, Accept(ref window, sequence));
            if (expected)
            {
                accepted.Add(sequence);
                highest = Math.Max(highest, sequence);
            }
        }
    }
}
