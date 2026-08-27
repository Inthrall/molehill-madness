using System;
using System.Collections.Generic;
using MoleSim.Match;

namespace Molehill.Online
{
    /// <summary>Where an online match has got to.</summary>
    public enum OnlineStage
    {
        /// <summary>Opening, joining or resuming. Nothing to show but that we are trying.</summary>
        Arriving = 0,

        /// <summary>In a lobby with empty seats, waiting for the rest.</summary>
        WaitingForPlayers = 1,

        /// <summary>This player's turn to plan. The only stage where the game wants input.</summary>
        Planning = 2,

        /// <summary>This player's plan is on its way to the relay.</summary>
        Sending = 3,

        /// <summary>Committed, waiting for the others. The stage a match spends its life in.</summary>
        WaitingForOthers = 4,

        /// <summary>Every plan is in hand and the round can be simulated.</summary>
        RoundReady = 5,

        /// <summary>Over, one way or another. <see cref="OnlineMatch.Trouble"/> says which.</summary>
        Done = 6,

        /// <summary>
        /// Somebody in this match is running a different build. Their plan is in a format this one
        /// does not speak, so simulating it would diverge rather than fail.
        /// </summary>
        Incompatible = 7,
    }

    /// <summary>
    /// One match, played apart.
    /// </summary>
    /// <remarks>
    /// Poll-driven on purpose. A game loop cannot await, and a request that blocks a frame is a
    /// stutter every player sees, so this keeps at most one call in flight, is asked every frame
    /// whether anything has come back, and never waits for anything. It has no engine types in it,
    /// which is what lets the whole online flow be tested against a real relay without booting Godot.
    ///
    /// The single most important thing in this file: when a round completes, every client feeds its
    /// simulation from the bytes the relay released, including its own plan, rather than from the
    /// Plan object it built locally. Submitting the local object would be one line shorter and would
    /// mean four clients feed their simulations from four sources that are only supposed to be
    /// identical. Any imperfection in the codec round trip would then show up as a desync in the
    /// field rather than as a failing test, and a desync is the most expensive bug this layer can
    /// cause. Reading everybody's plan back off the wire, mine included, removes the whole class.
    /// </remarks>
    public sealed class OnlineMatch
    {
        /// <summary>How often to ask about a round in Live pace, in seconds.</summary>
        private const double LiveGap = 1.0;

        /// <summary>
        /// How often in Anytime pace. Far apart, because a round window is measured in hours and a
        /// phone polling every second all day is a battery complaint rather than a feature. The push
        /// notification is what makes this acceptable, and it arrives in task 4.2.
        /// </summary>
        private const double AnytimeGap = 15.0;

        /// <summary>How long to wait after a call could not reach the relay.</summary>
        private const double RetryGap = 2.0;

        private readonly RelayClient _relay;
        private readonly Func<System.Threading.Tasks.Task<Reply<Seating>>>? _arrive;

        private System.Threading.Tasks.Task<Reply<Seating>>? _arriving;
        private System.Threading.Tasks.Task<Reply<Committed>>? _sending;
        private System.Threading.Tasks.Task<Reply<RoundRelease>>? _asking;

        private byte[]? _mine;
        private double _quiet;
        private List<Plan> _plans = new List<Plan>();
        private IReadOnlyList<int> _forfeited = Array.Empty<int>();

        private OnlineMatch(RelayClient relay, Func<System.Threading.Tasks.Task<Reply<Seating>>> arrive)
        {
            _relay = relay;
            _arrive = arrive;
            Stage = OnlineStage.Arriving;
        }

        /// <summary>Opens a new lobby and takes the host's seat in it.</summary>
        public static OnlineMatch Hosting(
            RelayClient relay, int playerCount, MatchPace pace, int windowSeconds = 0)
        {
            ArgumentNullException.ThrowIfNull(relay);

            return new OnlineMatch(relay, () => relay.Host(playerCount, pace, windowSeconds));
        }

        /// <summary>Takes a seat in somebody else's lobby.</summary>
        public static OnlineMatch Joining(RelayClient relay, string code)
        {
            ArgumentNullException.ThrowIfNull(relay);

            return new OnlineMatch(relay, () => relay.Join(code));
        }

        /// <summary>
        /// Comes back to a match this device is already in, using the token it kept.
        /// </summary>
        /// <remarks>
        /// This is what the client does on startup when it finds a stored match, and it is the whole
        /// of background resume. Joining instead would take a second seat or be refused as full.
        /// </remarks>
        public static OnlineMatch Resuming(RelayClient relay, string code, string token)
        {
            ArgumentNullException.ThrowIfNull(relay);

            return new OnlineMatch(relay, () => relay.Resume(code, token));
        }

        public OnlineStage Stage { get; private set; }

        /// <summary>The seat and the seed, once we have them.</summary>
        public Seating? Seating { get; private set; }

        public string Code => Seating?.Code ?? string.Empty;

        /// <summary>This player's platoon index, or -1 before we know it.</summary>
        public int Seat => Seating?.Seat ?? -1;

        public int PlayerCount => Seating?.PlayerCount ?? 0;

        public ulong Seed => Seating?.Seed ?? 0UL;

        public MatchPace Pace => Seating?.Pace ?? MatchPace.Live;

        /// <summary>Which round is being planned or resolved.</summary>
        public int Round { get; private set; } = 1;

        /// <summary>How many seats are still to commit, when that is what we are waiting for.</summary>
        public int WaitingOn { get; private set; }

        /// <summary>How many seats are taken while a lobby is filling.</summary>
        public int Seated => Seating?.Seated ?? 0;

        /// <summary>
        /// Every seat's plan for the round, in seat order, once <see cref="Stage"/> is RoundReady.
        /// </summary>
        public IReadOnlyList<Plan> Plans => _plans;

        /// <summary>
        /// Seats that ran out of window this round and did nothing.
        /// </summary>
        /// <remarks>
        /// Worth showing: a platoon that stood still because its player is asleep looks identical to one
        /// that stood still on purpose, and the difference matters to everybody else at the table.
        /// </remarks>
        public IReadOnlyList<int> Forfeited => _forfeited;

        /// <summary>When this round starts forfeiting, or null in Live pace where it never does.</summary>
        public DateTimeOffset? Deadline { get; private set; }

        /// <summary>Why the match ended, or the last thing that went wrong.</summary>
        public RelayOutcome Trouble { get; private set; }

        /// <summary>
        /// Whether a call failed and we are waiting to try it again.
        /// </summary>
        /// <remarks>
        /// Worth surfacing because it is the difference between "the others are still thinking" and
        /// "your train went into a tunnel", and a player who cannot tell those apart will assume the
        /// game is broken.
        /// </remarks>
        public bool Struggling { get; private set; }

        /// <summary>Whether the match is still going anywhere.</summary>
        public bool Live => Stage != OnlineStage.Done && Stage != OnlineStage.Incompatible;

        // ---- The loop ---------------------------------------------------------------

        /// <summary>
        /// Asked every frame. Starts calls when they are due, harvests them when they land, and
        /// returns immediately either way.
        /// </summary>
        public void Poll(double seconds)
        {
            _quiet += seconds;

            switch (Stage)
            {
                case OnlineStage.Arriving:
                    Arrive();
                    return;

                case OnlineStage.WaitingForPlayers:
                    FillingUp();
                    return;

                case OnlineStage.Sending:
                    Sending();
                    return;

                case OnlineStage.WaitingForOthers:
                    WaitingForOthers();
                    return;

                default:
                    // Planning, RoundReady, Done and Incompatible are all waiting on the game or on
                    // nothing, so there is nothing to poll.
                    return;
            }
        }

        /// <summary>
        /// Hands this player's plan over, as the bytes PlanCodec produced.
        /// </summary>
        public void Commit(byte[] plan)
        {
            ArgumentNullException.ThrowIfNull(plan);

            if (Stage != OnlineStage.Planning)
            {
                return;
            }

            _mine = plan;
            Stage = OnlineStage.Sending;
            _quiet = double.MaxValue;
        }

        /// <summary>
        /// Told by the game once it has fed the released plans in and resolved the round.
        /// </summary>
        public void RoundTaken()
        {
            if (Stage != OnlineStage.RoundReady)
            {
                return;
            }

            Round++;
            _plans = new List<Plan>();
            _forfeited = Array.Empty<int>();
            _mine = null;
            Stage = OnlineStage.Planning;
            _quiet = 0;
        }

        /// <summary>
        /// Reports the state hash for a round and forgets about it.
        /// </summary>
        /// <remarks>
        /// Deliberately not awaited and deliberately not retried. If two participants report
        /// different hashes then determinism broke on real hardware and the match is its own
        /// reproduction, but losing one report costs a diagnosis rather than a game, and a client
        /// that retried telemetry while a player waited would have its priorities backwards.
        /// </remarks>
        public void ReportHash(int round, ulong hash)
        {
            if (Seating is null)
            {
                return;
            }

            _ = _relay.ReportHash(Seating.Code, round, Seating.Token, hash);
        }

        /// <summary>Gives up on the match, without telling the relay, which does not care.</summary>
        public void Leave()
        {
            Stage = OnlineStage.Done;
        }

        // ---- Stages -----------------------------------------------------------------

        private void Arrive()
        {
            if (_arriving is null)
            {
                if (_quiet < RetryGap && Struggling)
                {
                    return;
                }

                _arriving = _arrive!();
                _quiet = 0;
                return;
            }

            if (!_arriving.IsCompleted)
            {
                return;
            }

            Reply<Seating> reply = Harvest(_arriving);
            _arriving = null;

            if (!reply.Ok)
            {
                Stumble(reply);
                return;
            }

            Seating = reply.Value;
            Round = Seating!.Round;

            // A resuming player can arrive into a match that is already running, so the started flag
            // decides whether there is anything to wait for rather than the fact of having arrived.
            Stage = Seating.Started ? OnlineStage.Planning : OnlineStage.WaitingForPlayers;
            _quiet = 0;
        }

        private void FillingUp()
        {
            if (_arriving is null)
            {
                if (_quiet < Gap())
                {
                    return;
                }

                // Reusing the seating call shape, because a lobby read answers the only question
                // being asked here: is everybody in yet.
                _arriving = Look();
                _quiet = 0;
                return;
            }

            if (!_arriving.IsCompleted)
            {
                return;
            }

            Reply<Seating> reply = Harvest(_arriving);
            _arriving = null;

            if (!reply.Ok)
            {
                Stumble(reply);
                return;
            }

            Seating = reply.Value;

            if (Seating!.Started)
            {
                Round = Seating.Round;
                Stage = OnlineStage.Planning;
            }

            _quiet = 0;
        }

        private void Sending()
        {
            if (_sending is null)
            {
                if (_quiet < RetryGap)
                {
                    return;
                }

                _sending = _relay.Commit(Seating!.Code, Round, Seating.Token, _mine!);
                _quiet = 0;
                return;
            }

            if (!_sending.IsCompleted)
            {
                return;
            }

            Reply<Committed> reply = Harvest(_sending);
            _sending = null;

            if (reply.Ok)
            {
                Struggling = false;
                WaitingOn = reply.Value!.WaitingOn;
                Stage = OnlineStage.WaitingForOthers;
                _quiet = double.MaxValue;
                return;
            }

            // Already committed is not a failure here. It means the plan arrived and the reply did
            // not, which is exactly what happens when a phone drops signal mid-request, and the
            // right response is to stop pushing and go and read the round.
            if (reply.Outcome == RelayOutcome.AlreadyCommitted)
            {
                Struggling = false;
                Stage = OnlineStage.WaitingForOthers;
                _quiet = double.MaxValue;
                return;
            }

            Stumble(reply);
        }

        private void WaitingForOthers()
        {
            if (_asking is null)
            {
                if (_quiet < Gap())
                {
                    return;
                }

                _asking = _relay.Round(Seating!.Code, Round);
                _quiet = 0;
                return;
            }

            if (!_asking.IsCompleted)
            {
                return;
            }

            Reply<RoundRelease> reply = Harvest(_asking);
            _asking = null;

            if (!reply.Ok)
            {
                Stumble(reply);
                return;
            }

            Struggling = false;
            RoundRelease release = reply.Value!;

            if (!release.Complete)
            {
                WaitingOn = release.WaitingOn;
                Deadline = release.Deadline;
                _quiet = 0;
                return;
            }

            Take(release);
        }

        /// <summary>
        /// Turns the released bytes into plans, refusing anything that is not a whole round.
        /// </summary>
        /// <remarks>
        /// The guards here matter more than they look. A round missing a seat would still simulate:
        /// the match would treat that platoon as having done nothing, produce a perfectly plausible
        /// result, and disagree with every other client from then on. That is a silent desync, so it
        /// is checked rather than trusted, even though the relay is supposed to make it impossible.
        /// </remarks>
        private void Take(RoundRelease release)
        {
            List<Plan> plans = new List<Plan>(release.Plans.Count);

            try
            {
                foreach (byte[] bytes in release.Plans)
                {
                    plans.Add(PlanCodec.Read(bytes));
                }
            }
            catch (PlanFormatException)
            {
                // Somebody is on a different build. Simulating their plan would diverge rather than
                // fail, so stop here instead.
                Trouble = RelayOutcome.Refused;
                Stage = OnlineStage.Incompatible;
                return;
            }

            bool[] answered = new bool[PlayerCount];

            foreach (Plan plan in plans)
            {
                if (plan.Seat < 0 || plan.Seat >= PlayerCount || answered[plan.Seat])
                {
                    Trouble = RelayOutcome.Refused;
                    Stage = OnlineStage.Incompatible;
                    return;
                }

                answered[plan.Seat] = true;
            }

            // A forfeited seat answered by running out of window, and it answers with nothing rather
            // than with an empty plan. This is why the check is "every seat accounted for" rather
            // than "a plan per seat", which would refuse every Anytime round somebody slept through.
            foreach (int seat in release.Forfeited)
            {
                if (seat < 0 || seat >= PlayerCount || answered[seat])
                {
                    Trouble = RelayOutcome.Refused;
                    Stage = OnlineStage.Incompatible;
                    return;
                }

                answered[seat] = true;
            }

            foreach (bool seat in answered)
            {
                if (!seat)
                {
                    Trouble = RelayOutcome.Refused;
                    Stage = OnlineStage.Incompatible;
                    return;
                }
            }

            _plans = plans;
            _forfeited = release.Forfeited;
            Deadline = release.Deadline;
            WaitingOn = 0;
            Stage = OnlineStage.RoundReady;
        }

        // ---- Plumbing ---------------------------------------------------------------

        private System.Threading.Tasks.Task<Reply<Seating>> Look()
        {
            Seating seating = Seating!;

            // The seat read rather than the lobby read, because it answers the same question and
            // returns the same shape, which keeps one harvesting path instead of two.
            return _relay.Resume(seating.Code, seating.Token);
        }

        private double Gap() => Pace == MatchPace.Anytime ? AnytimeGap : LiveGap;

        /// <summary>
        /// Decides whether a failed call is worth trying again or is the end of the match.
        /// </summary>
        private void Stumble<T>(Reply<T> reply)
        {
            Trouble = reply.Outcome;

            if (reply.WorthRetrying)
            {
                Struggling = true;
                _quiet = 0;
                return;
            }

            Struggling = false;
            Stage = OnlineStage.Done;
        }

        /// <summary>
        /// Reads a finished call's result without letting an unexpected exception reach a game loop.
        /// </summary>
        /// <remarks>
        /// RelayClient turns every expected failure into an outcome, so a fault here is a bug rather
        /// than a network. It still must not take the frame down: a crash in the middle of a match
        /// loses a player's round, and treating it as unreachable at least keeps them in the game
        /// long enough to see something.
        /// </remarks>
        private static Reply<T> Harvest<T>(System.Threading.Tasks.Task<Reply<T>> flight)
        {
            if (flight.IsFaulted || flight.IsCanceled)
            {
                return Reply.Bad<T>(RelayOutcome.Unreachable);
            }

            return flight.Result;
        }
    }
}
