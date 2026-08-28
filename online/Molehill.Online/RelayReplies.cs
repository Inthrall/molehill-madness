using System;
using System.Collections.Generic;

namespace Molehill.Online
{
    /// <summary>
    /// How a call to the relay turned out.
    /// </summary>
    /// <remarks>
    /// An enum rather than exceptions, because most of these are ordinary things that happen to
    /// players rather than faults: a mistyped code, a lobby that filled while you were reading it
    /// out, a train going into a tunnel. Throwing for those would make the calling code a pile of
    /// catch blocks in the middle of a game loop, and the interesting ones would be indistinguishable
    /// from a real bug.
    ///
    /// <see cref="Unreachable"/> in particular is expected. A phone loses signal, and the answer is
    /// to keep trying and show that we are trying, not to end the match.
    /// </remarks>
    public enum RelayOutcome
    {
        /// <summary>It worked.</summary>
        Ok = 0,

        /// <summary>The relay could not be reached, or did not answer in time. Try again.</summary>
        Unreachable = 1,

        /// <summary>No match has that code, or the code is not one the relay would ever issue.</summary>
        NoSuchMatch = 2,

        /// <summary>Every seat was taken before this player got there.</summary>
        Full = 3,

        /// <summary>This seat has already committed for this round.</summary>
        AlreadyCommitted = 4,

        /// <summary>The match has moved on, or has not got here yet. The reply carries the round.</summary>
        WrongRound = 5,

        /// <summary>The token does not own a seat in this match.</summary>
        NotYourSeat = 6,

        /// <summary>The relay refused it for a reason worth logging rather than showing.</summary>
        Refused = 7,

        /// <summary>
        /// This account is not old enough to be put among strangers.
        /// </summary>
        /// <remarks>
        /// Its own outcome rather than a Refused, because it is the only refusal in the whole client
        /// that a player is entitled to an explanation for. Everything else the relay says no to is a
        /// bug or a race; this one is a rule, and a queue button that failed silently on it would
        /// look broken to exactly the person it is protecting.
        /// </remarks>
        TooYoung = 8,
    }

    /// <summary>
    /// The whole of an account: an opaque id and the secret that owns it.
    /// </summary>
    /// <remarks>
    /// Worth keeping somewhere durable the moment it arrives, because the relay hands the secret over
    /// exactly once and has no way to reissue it. There is nothing in an account to recover it by, by
    /// design: no email for an under-threshold one, and the design says there must not be. Losing it
    /// costs a player nothing they can name, since an account carries no progress, no purchases and
    /// no friends, only permission to be put among strangers.
    /// </remarks>
    public sealed class AccountKey
    {
        public AccountKey(string id, string secret)
        {
            Id = id;
            Secret = secret;
        }

        public string Id { get; }

        public string Secret { get; }
    }

    /// <summary>Where a ticket has got to: still queueing, or seated in a match.</summary>
    public sealed class Place
    {
        public Place(int seconds, bool slow, Seating? seated)
        {
            Seconds = seconds;
            Slow = slow;
            Seated = seated;
        }

        /// <summary>How long this player has been in the pool.</summary>
        public int Seconds { get; }

        /// <summary>
        /// Whether the queue is thin enough that the other pace is worth offering.
        /// </summary>
        /// <remarks>
        /// The design's answer to an empty pool is Anytime rather than a better spinner. The relay
        /// says when, the player decides whether: changing somebody's pace out from under them would
        /// be answering a different question from the one they asked.
        /// </remarks>
        public bool Slow { get; }

        /// <summary>The seat, once the pool has found one. Null while still waiting.</summary>
        public Seating? Seated { get; }
    }

    /// <summary>A reply that either carries a value or explains why it does not.</summary>
    public readonly struct Reply<T>
    {
        internal Reply(RelayOutcome outcome, T? value)
        {
            Outcome = outcome;
            Value = value;
        }

        public RelayOutcome Outcome { get; }

        public T? Value { get; }

        public bool Ok => Outcome == RelayOutcome.Ok;

        /// <summary>Whether trying the same call again could plausibly work.</summary>
        /// <remarks>
        /// Only the transport failures. A mistyped code will still be mistyped in ten seconds, and
        /// retrying it forever is how a client ends up hammering a relay about nothing.
        /// </remarks>
        public bool WorthRetrying => Outcome == RelayOutcome.Unreachable;
    }

    /// <summary>Makes replies, so the generic type does not have to carry its own factories.</summary>
    public static class Reply
    {
        public static Reply<T> Good<T>(T value) => new Reply<T>(RelayOutcome.Ok, value);

        public static Reply<T> Bad<T>(RelayOutcome outcome) => new Reply<T>(outcome, default);
    }

    /// <summary>A seat in a match, and everything needed to start simulating it.</summary>
    /// <remarks>
    /// The seed is the shared world and the seat number is the platoon index every client agrees on.
    /// The token is the only credential in the game and is worth storing on the device, because it is
    /// what lets a player come back to a match after their phone went to sleep.
    /// </remarks>
    public sealed class Seating
    {
        public Seating(
            string code,
            int seat,
            string token,
            int playerCount,
            MatchPace pace,
            ulong seed,
            int seated,
            bool started,
            int round,
            int windowSeconds,
            DateTimeOffset? deadline)
        {
            Code = code;
            Seat = seat;
            Token = token;
            PlayerCount = playerCount;
            Pace = pace;
            Seed = seed;
            Seated = seated;
            Started = started;
            Round = round;
            WindowSeconds = windowSeconds;
            Deadline = deadline;
        }

        public string Code { get; }

        public int Seat { get; }

        public string Token { get; }

        public int PlayerCount { get; }

        public MatchPace Pace { get; }

        public ulong Seed { get; }

        /// <summary>How many seats are taken, including this one.</summary>
        public int Seated { get; }

        /// <summary>Whether every seat is filled, which is when a match can begin.</summary>
        public bool Started { get; }

        public int Round { get; }

        /// <summary>
        /// How long an Anytime round waits before the missing players forfeit. Zero in Live pace.
        /// </summary>
        public int WindowSeconds { get; }

        /// <summary>
        /// When this round starts forfeiting, or null when it never does.
        /// </summary>
        /// <remarks>
        /// Worth having on the client because a player who can see how long they have left behaves very
        /// differently from one who cannot, and Anytime pace is otherwise a screen with no urgency on it
        /// at all.
        /// </remarks>
        public DateTimeOffset? Deadline { get; }
    }

    /// <summary>How a match is paced, which the host picks up front.</summary>
    public enum MatchPace
    {
        /// <summary>Everybody present, one shared clock, resolution as soon as the last plan lands.</summary>
        Live = 0,

        /// <summary>Round windows measured in hours, with a forfeit at the end of one.</summary>
        Anytime = 1,
    }

    /// <summary>Who is in a lobby and what it is doing.</summary>
    public sealed class LobbyState
    {
        public LobbyState(string code, int playerCount, MatchPace pace, int seated, bool started, int round)
        {
            Code = code;
            PlayerCount = playerCount;
            Pace = pace;
            Seated = seated;
            Started = started;
            Round = round;
        }

        public string Code { get; }

        public int PlayerCount { get; }

        public MatchPace Pace { get; }

        public int Seated { get; }

        public bool Started { get; }

        public int Round { get; }
    }

    /// <summary>What has been said in a match, and where to carry on listening from.</summary>
    /// <remarks>
    /// The cursor comes back from the relay rather than being worked out here, so an empty reply
    /// cannot reset it and replay the whole conversation from the beginning.
    /// </remarks>
    public sealed class Chatter
    {
        public Chatter(long since, IReadOnlyList<(int Seat, Emote Emote)> said)
        {
            Since = since;
            Said = said;
        }

        public long Since { get; }

        public IReadOnlyList<(int Seat, Emote Emote)> Said { get; }
    }

    /// <summary>What the relay says after taking a plan.</summary>
    public sealed class Committed
    {
        public Committed(int seat, int waitingOn)
        {
            Seat = seat;
            WaitingOn = waitingOn;
        }

        public int Seat { get; }

        /// <summary>How many seats have still not committed.</summary>
        public int WaitingOn { get; }
    }

    /// <summary>
    /// A round, either still being waited on or complete with every plan in it.
    /// </summary>
    /// <remarks>
    /// <see cref="Plans"/> is empty until every seat has committed, and that is the relay's rule
    /// rather than this type's: handing back a partial round would let the last player to commit see
    /// what everybody else did first, which is the one thing simultaneous turns exist to prevent.
    /// </remarks>
    public sealed class RoundRelease
    {
        public RoundRelease(
            int round,
            bool complete,
            int waitingOn,
            IReadOnlyList<byte[]> plans,
            IReadOnlyList<int> forfeited,
            DateTimeOffset? deadline)
        {
            Round = round;
            Complete = complete;
            WaitingOn = waitingOn;
            Plans = plans;
            Forfeited = forfeited;
            Deadline = deadline;
        }

        public int Round { get; }

        public bool Complete { get; }

        public int WaitingOn { get; }

        /// <summary>Every seat's plan for the round, as the bytes they sent, in seat order.</summary>
        public IReadOnlyList<byte[]> Plans { get; }

        /// <summary>
        /// Seats that ran out of window and did nothing.
        /// </summary>
        /// <remarks>
        /// Not empty plans. The relay does not know what a plan looks like and must not learn, so a
        /// forfeit travels as the seat number and nothing else, and a client feeds its simulation
        /// nothing at all for that platoon. Which is exactly the platoon doing nothing.
        /// </remarks>
        public IReadOnlyList<int> Forfeited { get; }

        /// <summary>When the missing players forfeit, or null in Live pace where nobody does.</summary>
        public DateTimeOffset? Deadline { get; }

        public static RoundRelease Waiting(int round, int waitingOn, DateTimeOffset? deadline) =>
            new RoundRelease(
                round, complete: false, waitingOn,
                Array.Empty<byte[]>(), Array.Empty<int>(), deadline);
    }
}
