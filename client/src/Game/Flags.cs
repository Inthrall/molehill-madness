using Godot;

/// <summary>The command line switches the prototype understands.</summary>
/// <remarks>
/// Shared rather than duplicated, because the menu needs to know about <c>--demo</c> before the
/// match exists and about <c>--touch</c> before it knows which layout it is drawing.
/// </remarks>
public static class Flags
{
    public static bool Asked(string flag)
    {
        foreach (string argument in OS.GetCmdlineUserArgs())
        {
            if (argument == flag)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether to bring up the thumb controls, on a phone or on request.</summary>
    public static bool WantsTouch() => OS.HasFeature("mobile") || Asked("--touch");

    /// <summary>Whether the test driver is playing, which nothing about a real game should see.</summary>
    public static bool Driven() => Asked("--demo");

    /// <summary>
    /// A match to play, if one was named as <c>--seed=12345</c>.
    /// </summary>
    /// <remarks>
    /// Every match is a seed, so naming one replays that exact garden. Worth having for a
    /// playtest, where "that round was extraordinary, do it again" is otherwise unanswerable, and
    /// for getting at an outcome the driver only reaches on some seeds.
    /// </remarks>
    public static ulong? Seed()
    {
        const string named = "--seed=";

        foreach (string argument in OS.GetCmdlineUserArgs())
        {
            if (argument.StartsWith(named, System.StringComparison.Ordinal)
                && ulong.TryParse(argument.Substring(named.Length), out ulong seed))
            {
                return seed;
            }
        }

        return null;
    }

    /// <summary>
    /// Where the relay lives, as <c>--relay=http://127.0.0.1:7100</c>.
    /// </summary>
    /// <remarks>
    /// A switch rather than a constant, because two clients on one desk playing against each other
    /// through a local relay is the whole development loop for online play, and hard-coding a
    /// deployed address would make that impossible without a rebuild.
    ///
    /// The default is the local one for the same reason: a build with no relay flag is a build
    /// somebody is testing, and pointing it at a production address by default is how a development
    /// run ends up in a stranger's lobby.
    /// </remarks>
    public static string Relay()
    {
        const string named = "--relay=";

        foreach (string argument in OS.GetCmdlineUserArgs())
        {
            if (argument.StartsWith(named, System.StringComparison.Ordinal))
            {
                return argument.Substring(named.Length);
            }
        }

        return "http://127.0.0.1:7100";
    }

    /// <summary>
    /// A code to join straight away, as <c>--join=ABCDE</c>.
    /// </summary>
    /// <remarks>
    /// This is what a share link resolves to. The design's code-prefilling links land here rather
    /// than in a browser handshake, and it is also how two clients on one desk get into the same
    /// match without anybody typing.
    /// </remarks>
    public static string? Join()
    {
        const string named = "--join=";

        foreach (string argument in OS.GetCmdlineUserArgs())
        {
            if (argument.StartsWith(named, System.StringComparison.Ordinal))
            {
                return argument.Substring(named.Length);
            }
        }

        return null;
    }

    /// <summary>
    /// How long an Anytime round waits, as <c>--window=120</c> seconds.
    /// </summary>
    /// <remarks>
    /// The design gives the host the call, defaulting to a day. This exists mostly so the forfeit path
    /// can be watched without waiting one: a two-minute window makes it a thing you can see happen.
    /// </remarks>
    public static int? Window()
    {
        const string named = "--window=";

        foreach (string argument in OS.GetCmdlineUserArgs())
        {
            if (argument.StartsWith(named, System.StringComparison.Ordinal)
                && int.TryParse(argument.Substring(named.Length), out int seconds))
            {
                return seconds;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether to make a clip of the match when it finishes, as <c>--clip</c>.
    /// </summary>
    /// <remarks>
    /// A development switch. The player-facing flow is a share button on the scoreboard, which needs the
    /// encoders and the share sheet the plan puts alongside them; this is how the pipeline underneath
    /// gets exercised in the meantime, and it writes the result somewhere a person can open it.
    /// </remarks>
    public static bool Clip() => Asked("--clip");

    /// <summary>Whether to open a lobby at once, as <c>--host</c>.</summary>
    public static bool Host() => Asked("--host");

    /// <summary>
    /// Whether to go straight into the matchmaking pool, as <c>--matchmake</c>.
    /// </summary>
    /// <remarks>
    /// A development switch, and the same kind of switch <c>--host</c> was before the menu grew a
    /// button for hosting. The player-facing flow is one button on the menu and a screen to wait on,
    /// including the offer of the other pace when the pool is thin; this is how the plumbing under it
    /// gets driven end to end in the meantime, which for a pool means running two of them.
    ///
    /// It needs an account that has been through the age gate, because that is the whole point of
    /// the gate. A device that has not been asked comes straight back to the menu.
    /// </remarks>
    public static bool Matchmake() => Asked("--matchmake");

    /// <summary>
    /// How many platoons, as <c>--players=2</c>.
    /// </summary>
    /// <remarks>
    /// Only useful alongside <c>--host</c> or <c>--demo</c>, both of which walk past the menu where
    /// the choice is normally made. Without it a hosted lobby takes the default of four and then
    /// waits for two players who are never coming, which is a confusing way to spend an afternoon.
    /// </remarks>
    public static int? Players()
    {
        const string named = "--players=";

        foreach (string argument in OS.GetCmdlineUserArgs())
        {
            if (argument.StartsWith(named, System.StringComparison.Ordinal)
                && int.TryParse(argument.Substring(named.Length), out int players))
            {
                return players;
            }
        }

        return null;
    }

    /// <summary>
    /// How many seconds of frames to measure, as <c>--perf</c> or <c>--perf=30</c>.
    /// </summary>
    /// <remarks>
    /// A measurement rather than a display. It turns vertical sync off, so a run says what the
    /// machine can do rather than what the monitor allowed, and it quits with a verdict when the
    /// time is up. See <see cref="PerfProbe"/> and tools/scripts/perf.ps1.
    /// </remarks>
    public static double? Perf()
    {
        const string named = "--perf=";

        foreach (string argument in OS.GetCmdlineUserArgs())
        {
            if (argument == "--perf")
            {
                return DefaultPerfSeconds;
            }

            if (argument.StartsWith(named, System.StringComparison.Ordinal)
                && double.TryParse(
                    argument.Substring(named.Length),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double seconds)
                && seconds > 0d)
            {
                return seconds;
            }
        }

        return null;
    }

    /// <summary>Long enough to catch a few rounds and both halves of the cut.</summary>
    private const double DefaultPerfSeconds = 30d;

    /// <summary>
    /// A picture quality named outright, as <c>--quality=low</c>, or null to let the game decide.
    /// </summary>
    /// <remarks>
    /// The switch exists for measuring rather than for playing: comparing two settings honestly
    /// means running the same seed twice and choosing the setting from outside, and a run that
    /// picked its own quality would be comparing two machines' guesses instead.
    /// </remarks>
    public static string? QualityAsked()
    {
        const string named = "--quality=";

        foreach (string argument in OS.GetCmdlineUserArgs())
        {
            if (argument.StartsWith(named, System.StringComparison.Ordinal))
            {
                return argument.Substring(named.Length);
            }
        }

        return null;
    }
}
