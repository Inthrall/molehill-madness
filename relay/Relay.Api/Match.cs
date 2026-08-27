namespace Relay.Api;

/// <summary>How a match is paced, which the host picks up front.</summary>
public enum Pace
{
    /// <summary>Everybody on a socket, one shared planning timer, resolution the moment the last commit lands.</summary>
    Live = 0,

    /// <summary>Round windows measured in hours, with a forfeit at the end of one.</summary>
    Anytime = 1,
}

/// <summary>What a match is, from the relay's point of view.</summary>
/// <remarks>
/// Almost nothing, and that is the design working as intended. Because plans are inputs rather than
/// outcomes, "a whole four-player match is a seed plus a list of payloads". The relay holds the seed,
/// remembers who is in which seat, and passes payloads along. It does not know what a mole is.
/// </remarks>
public sealed record Match(
    string Code,
    int PlayerCount,
    Pace Pace,
    ulong Seed,
    DateTimeOffset OpenedAt,
    int Round,
    bool Started);

/// <summary>One seat, and the token that proves somebody owns it.</summary>
/// <remarks>
/// The token is the whole of authorisation in v1. It stops a player submitting for a seat that is
/// not theirs, which is the only spoofing the design's threat model leaves open: "you cannot submit
/// an illegal state, only illegal inputs, which every client's sim rejects identically". Accounts
/// and the age gate arrive later and do not change this.
/// </remarks>
public sealed record Seat(string Code, int Number, string Token, DateTimeOffset JoinedAt);

/// <summary>One seat's plan for one round, exactly as the client sent it.</summary>
/// <remarks>
/// <see cref="Payload"/> is opaque and stays opaque. The relay never parses a plan, never validates
/// one and never simulates: the plan the client wrote is the bytes the other clients get, and every
/// client's own simulation is what decides whether it was legal. That rule is why this service can
/// be boring, and it is worth defending against the temptation to peek, because the moment the relay
/// understands a plan it has a second implementation of the rules to keep in step with the first.
/// </remarks>
public sealed record Submission(string Code, int Round, int Seat, byte[] Payload, DateTimeOffset At);

/// <summary>What one client thought the world looked like at the end of one round.</summary>
public sealed record ReportedHash(int Round, int Seat, ulong Hash);

/// <summary>Whether the participants in one round agreed about what happened in it.</summary>
/// <remarks>
/// Disagreement is the interesting case and the reason the hashes are collected at all: every
/// client simulated the same round from the same inputs, so two different answers mean determinism
/// broke on somebody's hardware. The match itself is the reproduction.
/// </remarks>
public sealed record RoundAgreement(int Round, int Reported, bool Agreed, IReadOnlyList<ulong> Hashes);
