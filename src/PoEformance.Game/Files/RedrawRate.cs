namespace PoEformance.Game.Files;

/// <summary>
/// How often a picture is being redrawn, as frames per second over the last half second.
/// </summary>
/// <remarks>
/// ASKED FOR FROM THE LIVE CLIENT, in the model pane's own row: not the overlay's frame rate,
/// which the status bar already carries, but how many pictures of the monster the rasteriser
/// manages a second while it plays or turns - the number that says whether the cap it draws under
/// is the right one for this machine.
///
/// A WINDOW, NOT A RUNNING AVERAGE. A window opens on a redraw and closes on the first redraw
/// half a second or more later, and what it counted, over exactly the time it spanned, is what is
/// shown until the next one closes - so the number holds still long enough to be read and still
/// follows a change within a second. The redraw that opens a window is not counted in it: it is
/// the window's start, not a frame inside it, and counting it read a steady seven a second as
/// nearly nine over the first window of every drag.
///
/// AND IT IS ZERO AT REST - once nothing has been redrawn for <see cref="Rest"/>, which is two
/// windows rather than one. A picture at rest is not sixty frames a second, it is none, and a rate
/// that kept showing the last number would be reporting a drag that ended; but a picture drawn
/// less often than once a window is still being drawn - the top rung on a slow machine - and at
/// one window it would read as nothing between every two of its frames.
///
/// IN THIS LAYER because it is arithmetic on a clock, and arithmetic inside the overlay cannot be
/// tested; the overlay hands in ImGui's own time.
/// </remarks>
public sealed class RedrawRate
{
    /// <summary>How long a window is, in seconds.</summary>
    public const double Window = 0.5;

    /// <summary>How long without a redraw counts as being at rest, in seconds.</summary>
    public const double Rest = 2 * Window;

    private double _opened;
    private double _last = double.NegativeInfinity;
    private double _shown;
    private int _counted;

    /// <summary>Notes one redraw, at this many seconds.</summary>
    public void Redrawn(double now)
    {
        if (now - _last > Rest)
        {
            // A fresh start, after a rest or at the very beginning: this redraw opens the window,
            // and the number from before the rest is not the number for whatever is starting.
            _opened = now;
            _last = now;
            _counted = 0;
            _shown = 0;
            return;
        }

        _last = now;
        _counted++;

        double elapsed = now - _opened;
        if (elapsed >= Window)
        {
            _shown = _counted / elapsed;
            _counted = 0;
            _opened = now;
        }
    }

    /// <summary>The rate to show at this many seconds, in frames per second.</summary>
    public float At(double now) => now - _last > Rest ? 0f : (float)_shown;
}
