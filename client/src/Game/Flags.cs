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
}
