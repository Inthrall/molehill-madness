namespace Relay.Api;

/// <summary>What a host asks for when opening a lobby.</summary>
public sealed record OpenLobby(int PlayerCount, Pace Pace);

/// <summary>
/// What a host or a joiner gets back: enough to start simulating, and nothing else.
/// </summary>
/// <remarks>
/// The seat number is the platoon index every client agrees on, the seed is the ground they will all
/// generate independently, and the token is how this player proves which seat is theirs. There is
/// nothing about the state of the game in here because the relay does not have any.
/// </remarks>
public sealed record Joined(
    string Code,
    int Seat,
    string Token,
    int PlayerCount,
    string Pace,
    string Seed,
    int Seated,
    bool Started)
{
    public static Joined From(Match match, Seat seat, int seatsTaken) =>
        new Joined(
            match.Code,
            seat.Number,
            seat.Token,
            match.PlayerCount,
            match.Pace.ToString(),
            match.Seed.ToString(System.Globalization.CultureInfo.InvariantCulture),
            seatsTaken,
            seatsTaken >= match.PlayerCount);
}

public static class Limits
{
    /// <summary>
    /// The largest a plan may be. A whole four-player match is about sixty kilobytes of payload, so
    /// one seat's plan for one round is orders of magnitude under this; the cap is here to stop the
    /// relay being used as a file host rather than to constrain the game.
    /// </summary>
    public const int LargestPlan = 16 * 1024;
}
