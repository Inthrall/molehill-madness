using System;
using Godot;
using Molehill.Online;

/// <summary>
/// The client's one online match, and the device's memory of it.
/// </summary>
/// <remarks>
/// Static for the same reason <see cref="MatchSetup"/> is: a Godot scene change tears the old tree
/// down before the new one exists, so the menu, the code screen and the match cannot hand each other
/// anything. This holds the session across those changes and owns the single RelayClient, which
/// wants to outlive any one scene so its connections are not rebuilt between screens.
///
/// It also remembers the match on disk, which is what makes background resume possible. The token is
/// the only credential in the game and cannot be reissued: a player who loses it has lost their seat
/// with no way to prove it was theirs, so it is written down the moment it arrives rather than when
/// the match ends.
/// </remarks>
public static class Online
{
    /// <summary>Where the device remembers a match in progress.</summary>
    private const string Remembered = "user://match.cfg";

    private static RelayClient? _relay;

    /// <summary>The match being played apart, or null for a couch game.</summary>
    public static OnlineMatch? Match { get; private set; }

    /// <summary>Whether this is an online match at all.</summary>
    public static bool Playing => Match is not null;

    /// <summary>
    /// The one relay client, built on first use.
    /// </summary>
    /// <remarks>
    /// Lazy rather than eager because a couch game should not open a socket, and built once rather
    /// than per call because a fresh HttpClient for every request is the standard way to exhaust a
    /// machine's sockets.
    /// </remarks>
    public static RelayClient Relay =>
        _relay ??= new RelayClient(new Uri(Flags.Relay(), UriKind.Absolute));

    public static void Host(int playerCount, MatchPace pace, int windowSeconds = 0)
    {
        Match = OnlineMatch.Hosting(Relay, playerCount, pace, windowSeconds);
    }

    public static void Join(string code)
    {
        Match = OnlineMatch.Joining(Relay, code);
    }

    /// <summary>
    /// Whether this device has a match it could go back into.
    /// </summary>
    /// <remarks>
    /// Only whether one is written down, not whether it is still running. Checking that would mean a
    /// network call before the menu can draw itself, and a menu that waits on a relay to appear is
    /// worse than one that occasionally offers a match that has finished.
    /// </remarks>
    public static bool Remembers() => Recall(out _, out _);

    /// <summary>Picks up a match this device was already in, if it remembers one.</summary>
    public static bool Resume()
    {
        if (!Recall(out string code, out string token))
        {
            return false;
        }

        Match = OnlineMatch.Resuming(Relay, code, token);

        return true;
    }

    /// <summary>Puts the game back on the couch.</summary>
    public static void Forget()
    {
        Match?.Leave();
        Match = null;
        Erase();
    }

    /// <summary>
    /// Writes the match down, so closing the game is not the same as leaving it.
    /// </summary>
    /// <remarks>
    /// Called once the seat is known rather than on a timer. There is nothing else worth storing:
    /// the seed comes back with the seat, the round comes back with it too, and everything else is
    /// derived by simulating, which is the whole reason a match is a seed and a list of plans.
    /// </remarks>
    public static void Remember()
    {
        if (Match?.Seating is null)
        {
            return;
        }

        ConfigFile file = new ConfigFile();
        file.SetValue("match", "code", Match.Code);
        file.SetValue("match", "token", Match.Seating.Token);
        file.Save(Remembered);
    }

    /// <summary>
    /// Opens the socket that says when something has happened.
    /// </summary>
    /// <remarks>
    /// Called at the same moment the seat is written down, because that is the first moment there is
    /// a code and a token to open one with, and it is safe to call again: a session that is already
    /// listening keeps the socket it has rather than stacking a second one on top of it.
    ///
    /// For both paces. Anytime benefits as much as Live does while the app is actually open, and the
    /// only difference between them is how long the poll underneath waits, which the session works
    /// out for itself.
    /// </remarks>
    public static void Listen()
    {
        if (Match?.Seating is null || Match.Hearing || Relay.Relay is not Uri where)
        {
            return;
        }

        Match.Listen(new LiveDoorbell(where, Match.Code, Match.Seating.Token));
    }

    private static bool Recall(out string code, out string token)
    {
        code = string.Empty;
        token = string.Empty;

        ConfigFile file = new ConfigFile();

        if (file.Load(Remembered) != Error.Ok)
        {
            return false;
        }

        code = file.GetValue("match", "code", string.Empty).AsString();
        token = file.GetValue("match", "token", string.Empty).AsString();

        return code.Length > 0 && token.Length > 0;
    }

    private static void Erase()
    {
        if (FileAccess.FileExists(Remembered))
        {
            DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(Remembered));
        }
    }
}
