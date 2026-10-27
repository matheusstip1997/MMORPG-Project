using Game.Gameplay.Prediction;
using Shared.Protocol;

namespace GameServer.Tests.World;

/// <summary>
/// The client's press latch (fix/parry-feel; R-2.2-01, R-3.1-04): input frames are sampled at 20 Hz, buttons every
/// rendered frame. Before it, a right click that went down and up between two ticks never reached the server, and a
/// press was judged at the next tick's view clock, up to a tick after the player pressed.
/// </summary>
public sealed class InputPressLatchTests
{
    [Fact]
    public void A_tap_between_two_ticks_reaches_the_next_frame_once()
    {
        var latch = new InputPressLatch();
        latch.Frame(InputActions.None, 100.0);
        latch.Take(InputActions.None, 100.0, out _); // tick: nothing held
        latch.Frame(InputActions.Parry, 100.3); // down for one rendered frame
        latch.Frame(InputActions.None, 100.6); // up again before the tick

        Assert.Equal(InputActions.Parry, latch.Take(InputActions.None, 100.9, out _));
        Assert.Equal(InputActions.None, latch.Take(InputActions.None, 101.9, out _)); // released: the edge is one press
    }

    [Fact]
    public void A_press_keeps_the_view_clock_of_the_frame_it_went_down_in()
    {
        var latch = new InputPressLatch();
        latch.Frame(InputActions.Parry, 200.25);
        latch.Frame(InputActions.Parry, 200.6);

        Assert.Equal(InputActions.Parry, latch.Take(InputActions.Parry, 200.95, out double view));
        Assert.Equal(200.25, view);
        // Still held at the next tick: no new press, the current clock.
        Assert.Equal(InputActions.Parry, latch.Take(InputActions.Parry, 201.95, out view));
        Assert.Equal(201.95, view);
    }

    [Fact]
    public void Held_buttons_pass_through_and_a_reset_forgets_a_pending_press()
    {
        var latch = new InputPressLatch();
        Assert.Equal(InputActions.Attack, latch.Take(InputActions.Attack, 10.0, out double view));
        Assert.Equal(10.0, view);

        latch.Frame(InputActions.Dodge, 11.0);
        latch.Reset();
        Assert.Equal(InputActions.None, latch.Take(InputActions.None, 11.5, out view));
        Assert.Equal(11.5, view);
    }
}
