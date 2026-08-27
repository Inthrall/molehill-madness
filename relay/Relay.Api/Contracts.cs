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
///
/// The round is here so that this one reply is everything a client needs to start playing, whether
/// it has just joined or is coming back to a match after its phone went to sleep. Resume is the case
/// that makes it necessary: the round is the only one of these values a returning player cannot have
/// kept from last time.
/// </remarks>
public sealed record Joined(
    string Code,
    int Seat,
    string Token,
    int PlayerCount,
    string Pace,
    string Seed,
    int Seated,
    bool Started,
    int Round)
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
            seatsTaken >= match.PlayerCount,
            match.Round);
}

/// <summary>
/// What a client reports at the end of a round.
/// </summary>
/// <remarks>
/// The hash travels as a string for the same reason the seed does: it is an unsigned 64-bit number,
/// and half of them do not fit in the signed integer a JSON number is often read as.
/// </remarks>
public sealed record ReportHash(string Hash);

/// <summary>Turns a pile of reported hashes into a per-round verdict.</summary>
public static class Agreement
{
    public static IReadOnlyList<RoundAgreement> Of(IReadOnlyList<ReportedHash> reported) =>
        reported
            .GroupBy(hash => hash.Round)
            .OrderBy(round => round.Key)
            .Select(round =>
            {
                ulong[] hashes = round.Select(hash => hash.Hash).ToArray();

                // One report cannot disagree with anything, so a round only half in counts as
                // agreed so far. Saying otherwise would flag every match still being played.
                return new RoundAgreement(
                    round.Key, hashes.Length, hashes.Distinct().Count() <= 1, hashes);
            })
            .ToArray();
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
