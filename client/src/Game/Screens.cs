using Godot;

/// <summary>
/// Where a development run puts its window.
/// </summary>
/// <remarks>
/// Anything that has to be looked at, measured or recorded needs a window on a screen, and on a
/// desk with monitors on it that window lands on top of whatever somebody was doing. The habit up
/// to now has been <c>--position 9000,9000</c>, off the side of the world, and it does not work:
/// Windows will not leave a window entirely outside the virtual desktop and puts it back somewhere
/// visible, and minimising it instead stops the frames being drawn at all, which is worse, because
/// the run still produces numbers and they are numbers about an idle process.
///
/// So the window goes to the smallest screen, which is the laptop's own panel on every machine
/// this has met, and never to the monitors somebody is working on.
///
/// Godot decides this rather than the script that starts the run, and that is the point of the
/// file. Windows reports screen bounds to a process in the units that process is aware of, while
/// Godot places windows in its own: a position worked out in PowerShell and passed to
/// <c>--position</c> asked for 2890,2160 and arrived at 3468,2592, a factor of 1.2 nobody had put
/// there. Godot's own screen numbers and Godot's own window position cannot disagree.
/// </remarks>
public static class Screens
{
    private static bool _moved;

    /// <summary>
    /// Puts the window on the machine's smallest screen, if the command line asked for it.
    /// </summary>
    /// <remarks>
    /// Once per process. A scene change should not move a window somebody has since dragged
    /// somewhere they wanted it.
    /// </remarks>
    public static void ToThePanelIfAsked()
    {
        if (_moved || !(Flags.Asked("--panel") || Flags.Perf() is not null))
        {
            return;
        }

        _moved = true;
        ToThePanel();
    }

    /// <summary>Moves the window to the smallest screen, and answers which one that was.</summary>
    public static int ToThePanel()
    {
        int screens = DisplayServer.GetScreenCount();
        int smallest = 0;
        long least = long.MaxValue;

        for (int screen = 0; screen < screens; screen++)
        {
            Vector2I size = DisplayServer.ScreenGetSize(screen);
            long area = (long)size.X * size.Y;

            if (area < least)
            {
                least = area;
                smallest = screen;
            }
        }

        DisplayServer.WindowSetCurrentScreen(smallest);
        DisplayServer.WindowSetPosition(DisplayServer.ScreenGetUsableRect(smallest).Position);

        // The window is never resized to fit. A run measures or records the resolution it was asked
        // for, and one that quietly did something smaller because it did not fit the panel would be
        // a misleading answer rather than a missing one.
        return smallest;
    }
}
