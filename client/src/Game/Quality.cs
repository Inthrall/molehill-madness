using Godot;

/// <summary>
/// How much picture the machine is being asked for.
/// </summary>
/// <remarks>
/// Two settings rather than five. Everything expensive in this client is expensive in the same
/// place, the fragment shader that turns the cell grid into ground, and a row of sliders over one
/// cost would be four ways of saying the same thing to a player who has no way of telling which one
/// mattered.
///
/// Chosen for the player rather than by them, because the game is wordless and a settings screen
/// that has to explain a trade-off is a screen this game cannot draw. The switch is there for
/// measuring: comparing two settings honestly means running the same seed twice and naming the
/// setting from outside, which a build that picked its own could not do.
///
/// Nothing selects Low on its own yet, and the reason is written out at the decision itself.
/// </remarks>
public static class Quality
{
    /// <summary>Which of the two pictures is being drawn.</summary>
    public enum Level
    {
        /// <summary>Everything, for a machine with the room for it.</summary>
        High,

        /// <summary>The same picture drawn with fewer samples, for a machine without.</summary>
        Low,
    }

    private static Level? _chosen;

    /// <summary>The setting in force.</summary>
    /// <remarks>
    /// Worked out once and remembered, because it is read from a draw call and a decision that
    /// re-reads the command line sixty times a second is a decision made badly.
    /// </remarks>
    public static Level Chosen()
    {
        if (_chosen is not null)
        {
            return _chosen.Value;
        }

        string? asked = Flags.QualityAsked();

        if (asked is not null)
        {
            _chosen = asked.ToLowerInvariant() == "low" ? Level.Low : Level.High;

            return _chosen.Value;
        }

        // High everywhere, including on a phone, and that is deliberately not what it was written
        // to be. The obvious rule is that a handheld GPU draws the same fragments with less to draw
        // them with, so a phone should take the cheaper picture; the reason it does not is that
        // nothing has yet measured the cheaper picture being cheaper.
        //
        // On the machine this was built on, three runs of each setting at 1920x1080 came out at 559
        // and 503 frames a second, and three runs of the high setting alone ranged from 456 to 617.
        // The gap between the settings is inside the spread of one of them, so the five tap version
        // is not measurably faster here, and shipping it as the default on the one platform that
        // cannot be checked would be a picture given away for a saving nobody has seen.
        //
        // What settles it is a phone, and the switch exists so that it can be: run the probe on a
        // device at each setting and read the gpu column. See docs/perf.md.
        _chosen = Level.High;

        return _chosen.Value;
    }

    /// <summary>
    /// How many samples of the cell field the ground shader takes for each fragment.
    /// </summary>
    /// <remarks>
    /// This is the whole of the difference between the two settings, and it is a fair trade rather
    /// than a corner cut. Nine taps in a square is what turns a staircase of cells into a curve;
    /// five in a cross reaches the same distance with a little less rounding at the diagonals,
    /// which on ground drawn at four cells to the metre is not something anybody has been able to
    /// point at in a frame, and it is four fewer texture reads on every fragment of the screen.
    /// </remarks>
    public static int BlurTaps() => Chosen() == Level.Low ? 5 : 9;
}
