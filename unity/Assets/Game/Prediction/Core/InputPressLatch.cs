#nullable enable
using Shared.Protocol;

namespace Game.Gameplay.Prediction
{
    /// <summary>
    /// Keeps the action buttons pressed between two client ticks (R-2.2-01, R-3.1-04). Input frames are sampled once per
    /// 20 Hz tick, but the buttons are read every rendered frame: a click that goes down and up between two ticks would
    /// read as never held at the tick, and the press would be lost. A button that went down since the last tick counts
    /// as held for the next frame, so the simulation still sees its edge.
    /// <para>
    /// The press also keeps the view clock of the frame it went down in: the server judges a parry or a dodge on that
    /// clock (R-3.1-03, R-3.3-02), and the next tick can be up to a tick later than the press.
    /// </para>
    /// Allocation-free; one thread (the client's main loop).
    /// </summary>
    public sealed class InputPressLatch
    {
        private InputActions _heldLastFrame;
        private InputActions _pressed;
        private double _pressRenderTick;
        private bool _hasPress;

        /// <summary>
        /// Once per rendered frame, before the frame's ticks run: the buttons held now and the render tick the player sees
        /// (the interpolation clock the next frame's view tick comes from).
        /// </summary>
        public void Frame(InputActions held, double renderTick)
        {
            var down = held & ~_heldLastFrame;
            _heldLastFrame = held;
            if (down == InputActions.None)
                return;
            _pressed |= down;
            if (!_hasPress)
            {
                _hasPress = true;
                _pressRenderTick = renderTick;
            }
        }

        /// <summary>
        /// Once per client tick: the buttons for the input frame (<paramref name="held"/> plus every button pressed since
        /// the last tick), and the render tick for its view clock (the first press's when there was one, else
        /// <paramref name="renderTick"/>).
        /// </summary>
        public InputActions Take(InputActions held, double renderTick, out double viewRenderTick)
        {
            var actions = held | _pressed;
            viewRenderTick = _hasPress && _pressRenderTick < renderTick ? _pressRenderTick : renderTick;
            _pressed = InputActions.None;
            _hasPress = false;
            return actions;
        }

        public void Reset()
        {
            _heldLastFrame = InputActions.None;
            _pressed = InputActions.None;
            _hasPress = false;
        }
    }
}
