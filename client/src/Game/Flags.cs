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

    /// <summary>Whether to open a lobby at once, as <c>--host</c>.</summary>
    public static bool Host() => Asked("--host");

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
}
