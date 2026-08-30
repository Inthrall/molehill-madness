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
        Drop();

        Match = OnlineMatch.Hosting(Relay, playerCount, pace, windowSeconds);
    }

    public static void Join(string code)
    {
        Drop();

        Match = OnlineMatch.Joining(Relay, code);
    }

    /// <summary>
    /// The one button: into the pool, and play whoever turns up.
    /// </summary>
    /// <remarks>
    /// The only way into a match with strangers, and the only thing in the game that needs an
    /// account. The account is made on the way in if this device has never needed one, and written
    /// down as it arrives, because the relay hands the secret over once and cannot hand it over
    /// again.
    ///
    /// A device that has not been through the age gate does not get as far as the relay: the session
    /// comes back finished, with TooYoung on it, and the caller sends the player to the gate. That
    /// check runs here as well as at the relay, and only the relay's one is a gate.
    /// </remarks>
    public static void Matchmake(int playerCount, MatchPace pace)
    {
        Drop();

        Match = OnlineMatch.Matchmaking(
            Relay, RelayAccount, Player.Band, playerCount, pace, Player.RememberRelay);
    }

    /// <summary>Whether the one button is worth offering at all on this device.</summary>
    public static bool CanMeetStrangers => Allowed.Matchmaking(Player.Band);

    private static AccountKey? RelayAccount => Player.RelayAccount;

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

        Drop();

        Match = OnlineMatch.Resuming(Relay, code, token);

        return true;
    }

    /// <summary>
    /// Stops playing this online match, and keeps the seat.
    /// </summary>
    /// <remarks>
    /// Called before any new session replaces the old one, which is what stops them piling up. A
    /// session that is dropped on the floor rather than left keeps its doorbell dialling and its
    /// place in the matchmaking pool: the doorbell's own notes warn that "a reconnect loop that
    /// outlived the match it was listening to would carry on dialling a finished game for as long as
    /// the process ran", and an abandoned ticket has the pool seat somebody into a match nobody is
    /// coming to. Finishing a match and starting another used to add one of each, every time.
    /// </remarks>
    /// <remarks>
    /// The one to reach for when something went wrong on the way to the relay, or when the player
    /// simply wants to be on the couch instead. The session goes, which hands back a pool ticket and
    /// stops the doorbell, and the written-down seat stays where it is.
    /// </remarks>
    public static void Drop()
    {
        Match?.Leave();
        Match = null;
    }

    /// <summary>
    /// Gives the seat up for good.
    /// </summary>
    /// <remarks>
    /// Irreversible, and worth being deliberate about: the token is handed over once and cannot be
    /// reissued, so a player who loses it has lost their seat with no way to prove it was theirs.
    /// </remarks>
    public static void Forget()
    {
        Drop();
        Erase();
    }

    /// <summary>
    /// Decides which of those a finished session has earned.
    /// </summary>
    /// <remarks>
    /// Every failure path used to erase, which threw away seats that were perfectly alive. Turning
    /// the wifi off during an Anytime match and pressing continue deleted the credential for a match
    /// still sitting on the relay; so did mistyping a join code, which took an unrelated saved match
    /// with it; and so did starting an ordinary couch game.
    ///
    /// Two things save it now. A relay we could not reach says nothing about whether the seat is
    /// still there, so that keeps it. And a session that is not the one written down cannot speak
    /// for it at all: a failed attempt at somebody else's code is not evidence about the match this
    /// device was already in.
    /// </remarks>
    public static void Finished(OnlineMatch match)
    {
        if (match is null || match.Trouble == RelayOutcome.Unreachable || !IsRemembered(match))
        {
            Drop();
            return;
        }

        Forget();
    }

    /// <summary>Whether that session is the match this device has written down.</summary>
    private static bool IsRemembered(OnlineMatch match) =>
        Recall(out string code, out string _)
        && match.Code.Length > 0
        && string.Equals(code, match.Code, StringComparison.Ordinal);

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
        // Guarded on having a doorbell rather than on its socket being up. Hearing goes false for
        // the whole of a connect and every reconnect backoff, and this is called every frame while
        // the match is arriving, so guarding on it built a new doorbell per frame and disposed the
        // one that was still dialling.
        if (Match?.Seating is null || Match.Listening || Relay.Relay is not Uri where)
        {
            return;
        }

        Match.Listen(new LiveDoorbell(where, Match.Code, Match.Seating.Token));
    }

    /// <summary>
    /// Tells the relay this device's age band, when there is an account to tell it about.
    /// </summary>
    /// <remarks>
    /// Nothing used to call this, which left the band a thing the relay was told exactly once, when
    /// the account was made. A child's account therefore stayed a child's account for ever: they
    /// turn thirteen, the local gate notices the answer could have changed and asks again, the
    /// device records an adult, and the relay carries on refusing the stranger pool with no way for
    /// anybody to correct it. The comment on the relay's own endpoint said the client re-asks and
    /// sends the new answer, and the sending half did not exist.
    ///
    /// Safe to call whenever the answer changes. A device with no account has nothing to update, and
    /// one that has never been asked has nothing worth saying.
    /// </remarks>
    public static void PushBand()
    {
        if (Player.RelayAccount is not AccountKey account || Player.Band == AgeBand.Unknown)
        {
            return;
        }

        // Not awaited. The band is already recorded on the device, the screen that asked has moved
        // on, and a failure here costs the player nothing until they next press the one button that
        // needs it, by which time this will have been called again.
        _ = Relay.SetBand(account, Player.Band);
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
