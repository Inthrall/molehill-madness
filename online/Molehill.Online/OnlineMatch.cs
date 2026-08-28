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

        /// <summary>
        /// How often to ask while a socket is up, in seconds.
        /// </summary>
        /// <remarks>
        /// Ten, which is a safety net rather than a poll. The socket says when a round is ready, so
        /// the only job left for the timer is to catch the case where a notice was missed while the
        /// connection was down and came back before anybody noticed. Ten seconds of extra latency in
        /// a case that should not happen is a fair price for one call every ten seconds instead of
        /// one a second.
        /// </remarks>
        private const double SocketGap = 10.0;

        private readonly RelayClient _relay;
        private readonly Func<System.Threading.Tasks.Task<Reply<Seating>>>? _arrive;

        private System.Threading.Tasks.Task<Reply<Seating>>? _arriving;
        private System.Threading.Tasks.Task<Reply<Committed>>? _sending;
        private System.Threading.Tasks.Task<Reply<RoundRelease>>? _asking;

        private System.Threading.Tasks.Task<Reply<Chatter>>? _listening;
        private long _heardUpTo;
        private double _sinceHeard;

        private LiveDoorbell? _bell;
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
            Elapsed += seconds;

            if (!Live)
            {
                // Finished, however it finished. The socket is no use to a match that is over and a
                // reconnect loop nobody stopped would outlive the game.
                Hush();
            }
            else if (_bell is not null && _bell.Rang())
            {
                // Something happened. Ask at once rather than at the end of whatever gap was left,
                // which is the entire point of having a socket: four phones see the round at the
                // same moment rather than up to a poll apart.
                _quiet = double.MaxValue;
            }

            Listen(seconds);

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
            Hush();
            Stage = OnlineStage.Done;
        }

        /// <summary>
        /// Takes a doorbell, and with it responsibility for turning it off.
        /// </summary>
        /// <remarks>
        /// Handed in rather than built here, so that nothing in this class has to know how to open a
        /// socket and the whole online flow stays testable against an in-process relay. Ownership
        /// comes with it: a reconnect loop that outlived the match it was listening to would carry on
        /// dialling a finished game for as long as the process ran.
        /// </remarks>
        public void Listen(LiveDoorbell bell)
        {
            ArgumentNullException.ThrowIfNull(bell);

            Hush();

            _bell = bell;
            _bell.Start();
        }

        /// <summary>Whether a socket is up, which is only ever of interest to a log.</summary>
        public bool Hearing => _bell is not null && _bell.Listening;

        private void Hush()
        {
            _bell?.Dispose();
            _bell = null;
        }

        // ---- Saying something -------------------------------------------------------

        /// <summary>What the other platoons are currently saying.</summary>
        public Conversation Chat { get; } = new Conversation();

        /// <summary>
        /// Seconds since this session started, which is the clock emotes are timed on.
        /// </summary>
        /// <remarks>
        /// A local clock rather than the relay's or the simulation's. How long a picture stays on
        /// screen is a presentation decision, and hanging it off a shared clock would mean an emote
        /// sent while somebody was in a tunnel arriving already expired.
        /// </remarks>
        public double Elapsed { get; private set; }

        /// <summary>
        /// Says something, and forgets about it.
        /// </summary>
        /// <remarks>
        /// Not awaited and not retried. The relay limits how often a seat may speak, so a refusal is
        /// an expected outcome of tapping twice rather than a failure, and a client that retried
        /// chatter would be arguing with a rate limit on the player's behalf.
        /// </remarks>
        public void Say(Emote emote)
        {
            if (Seating is null)
            {
                return;
            }

            // Shown here at once rather than waiting for it to come back from the relay. A wheel that
            // does not respond until a round trip completes feels broken, and this is the one part of
            // the game where nothing depends on every client agreeing.
            Chat.Heard(Seating.Seat, emote, Elapsed);

            _ = _relay.Say(Seating.Code, Seating.Token, emote);
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

        /// <summary>
        /// Picks up anything said since last time, on its own cadence and its own request.
        /// </summary>
        /// <remarks>
        /// Separate from the round poll because the times a player most wants to say something are
        /// while everybody is still planning, and the round is not being polled then. It is also the
        /// one call in here whose failure is worth nothing: a lost emote is a lost emote.
        ///
        /// The cadence follows the pace like everything else, so an Anytime match is not asking after
        /// chatter every second for a day.
        /// </remarks>
        private void Listen(double seconds)
        {
            if (Seating is null || !Live)
            {
                return;
            }

            _sinceHeard += seconds;

            if (_listening is null)
            {
                if (_sinceHeard < Gap())
                {
                    return;
                }

                _sinceHeard = 0;
                _listening = _relay.Listen(Seating.Code, _heardUpTo);
                return;
            }

            if (!_listening.IsCompleted)
            {
                return;
            }

            Reply<Chatter> reply = Harvest(_listening);
            _listening = null;

            if (!reply.Ok || reply.Value is not Chatter chatter)
            {
                // Nothing to do about it and nothing to report. Chatter is the one thing here that is
                // fine to lose.
                return;
            }

            _heardUpTo = chatter.Since;

            foreach ((int seat, Emote emote) in chatter.Said)
            {
                // Except this client's own, which was shown the moment the wheel was tapped and
                // would otherwise restart its own timer when it came back round.
                if (seat != Seating.Seat)
                {
                    Chat.Heard(seat, emote, Elapsed);
                }
            }
        }

        private double Gap()
        {
            double gap = Pace == MatchPace.Anytime ? AnytimeGap : LiveGap;

            // The larger of the two, never the socket's own figure. Anytime already polls further
            // apart than the safety net does, and a socket that made a client ask more often than it
            // would have without one would be an optimisation with the sign the wrong way round.
            return _bell is not null && _bell.Listening ? Math.Max(gap, SocketGap) : gap;
        }

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
