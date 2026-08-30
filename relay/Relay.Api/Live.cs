using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Relay.Api;

/// <summary>
/// Who is listening to a match, and how to tell them something.
/// </summary>
/// <remarks>
/// A doorbell rather than a delivery van, and that is the whole design. The socket never carries a
/// plan: it says "round four is ready" and the client fetches round four through the same endpoint
/// it would have polled. Nothing that matters travels over it, so a socket that drops costs a second
/// of latency and nothing else, and the polling path stays the truth rather than becoming a
/// forgotten branch that only runs when something has already gone wrong.
///
/// That is also why this arrived after the game was already playable apart. Live pace worked by
/// polling from the first day and the plan calls the hub an optimisation on top of a protocol that
/// works, which it is. What it buys is the moment of resolution: four phones polling on their own
/// second see the round up to a second apart, and in a game whose entire premise is that everybody
/// moves at once, that is the one place the seam shows.
/// </remarks>
public sealed class LiveHub
{
    private readonly ConcurrentDictionary<string, Listeners> _matches =
        new ConcurrentDictionary<string, Listeners>(StringComparer.Ordinal);

    /// <summary>Every match somebody is listening to.</summary>
    public IReadOnlyCollection<string> Watched() => _matches.Keys.ToArray();

    /// <summary>Which seats of a match have somebody on the other end of a socket.</summary>
    public IReadOnlyCollection<int> Present(string code) =>
        _matches.TryGetValue(code, out Listeners? listeners)
            ? listeners.Seats()
            : Array.Empty<int>();

    /// <summary>
    /// Holds a socket open until the far end goes away.
    /// </summary>
    /// <remarks>
    /// The receive loop is not optional even though nothing a client sends is read. A WebSocket close
    /// arrives as a received frame, so a server that never receives never learns that anybody has
    /// hung up, and the socket sits in the list until a send fails. Anything else that arrives is
    /// dropped on the floor: this is a one-way channel by design, since a client with something to
    /// say has endpoints for it and a channel that accepts commands is a second way into the relay
    /// to get authorisation wrong on.
    /// </remarks>
    public async Task Hold(string code, int seat, WebSocket socket, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(socket);

        Listener listener = new Listener(seat, socket);
        Listeners listeners = _matches.GetOrAdd(code, _ => new Listeners());

        listeners.Add(listener);

        try
        {
            byte[] ignored = new byte[256];

            while (socket.State == WebSocketState.Open && !cancel.IsCancellationRequested)
            {
                WebSocketReceiveResult heard = await socket
                    .ReceiveAsync(new ArraySegment<byte>(ignored), cancel)
                    .ConfigureAwait(false);

                if (heard.MessageType == WebSocketMessageType.Close)
                {
                    // Answered rather than merely noted. A close is a handshake, and a server that
                    // hangs up without finishing it leaves the client waiting on a socket that has
                    // been disposed underneath it: an ordinary goodbye then surfaces at the other
                    // end as a crash. Found by a test doing nothing more exotic than closing.
                    await socket
                        .CloseOutputAsync(
                            WebSocketCloseStatus.NormalClosure, null, CancellationToken.None)
                        .ConfigureAwait(false);

                    break;
                }
            }
        }
        catch (WebSocketException)
        {
            // A connection that died rather than closed, which is what a phone going into a tunnel
            // looks like. Identical handling: the listener goes, and the watcher notices the seat is
            // no longer present.
        }
        catch (OperationCanceledException)
        {
            // The relay is shutting down.
        }
        finally
        {
            listeners.Remove(listener);

            if (listeners.Empty)
            {
                // Nobody left listening, so the watcher stops looking at this match. Racy in
                // principle, since somebody could join between the check and the removal; harmless
                // in practice, because the next Hold puts it straight back and the only cost of
                // being wrong for a moment is a round announced a tick late.
                _matches.TryRemove(code, out _);
            }

            listener.Dispose();
        }
    }

    /// <summary>Says one thing to everybody listening to a match.</summary>
    public async Task Tell(string code, byte[] message)
    {
        if (!_matches.TryGetValue(code, out Listeners? listeners))
        {
            return;
        }

        foreach (Listener listener in listeners.All())
        {
            await listener.Say(message).ConfigureAwait(false);
        }
    }

    /// <summary>One socket, and the seat behind it.</summary>
    private sealed class Listener : IDisposable
    {
        private readonly SemaphoreSlim _sending = new SemaphoreSlim(1, 1);
        private readonly WebSocket _socket;

        public Listener(int seat, WebSocket socket)
        {
            Seat = seat;
            _socket = socket;
        }

        public int Seat { get; }

        /// <summary>
        /// Sends, one at a time.
        /// </summary>
        /// <remarks>
        /// A WebSocket throws if a second send starts before the first has finished, and while the
        /// watcher is the only thing that broadcasts today, "only one caller" is a property nobody
        /// can see from here and the next caller will not know they were supposed to check. A
        /// semaphore is three lines and removes the whole class.
        /// </remarks>
        public async Task Say(byte[] message)
        {
            if (_socket.State != WebSocketState.Open)
            {
                return;
            }

            // Inside the guard, because the wait is the thing most likely to throw. A listener is
            // removed and disposed by its own receive loop the moment its socket goes, and a
            // broadcast walks a snapshot taken before that happened, so the semaphore can be
            // disposed between the state check above and this line. It used to throw straight out
            // of Say and out of Tell with it, which stopped the broadcast: one player hanging up at
            // the wrong instant cost everybody else in the match that notice.
            try
            {
                await _sending.WaitAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            try
            {
                await _socket
                    .SendAsync(
                        new ArraySegment<byte>(message),
                        WebSocketMessageType.Text,
                        endOfMessage: true,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
                // Gone between the state check and the send. The receive loop on this socket is
                // about to notice and take the listener out of the list.
            }
            catch (ObjectDisposedException)
            {
                // The same, one step further along.
            }
            finally
            {
                _sending.Release();
            }
        }

        public void Dispose() => _sending.Dispose();
    }

    /// <summary>The listeners on one match, safe to walk while somebody joins or leaves.</summary>
    private sealed class Listeners
    {
        private readonly object _gate = new object();
        private readonly List<Listener> _all = new List<Listener>();

        public bool Empty
        {
            get
            {
                lock (_gate)
                {
                    return _all.Count == 0;
                }
            }
        }

        public void Add(Listener listener)
        {
            lock (_gate)
            {
                _all.Add(listener);
            }
        }

        public void Remove(Listener listener)
        {
            lock (_gate)
            {
                _all.Remove(listener);
            }
        }

        /// <summary>A copy, because sending is slow and holding a lock across it would serialise it.</summary>
        public Listener[] All()
        {
            lock (_gate)
            {
                return _all.ToArray();
            }
        }

        public int[] Seats()
        {
            lock (_gate)
            {
                return _all.Select(listener => listener.Seat).Distinct().ToArray();
            }
        }
    }
}

/// <summary>What the hub says, and what it looks like on the wire.</summary>
public static class LiveNotice
{
    /// <summary>Somebody took a seat in the lobby.</summary>
    public static byte[] Seated(int seated) =>
        Wire.Json(writer =>
        {
            writer.WriteString("kind", "seated");
            writer.WriteNumber("seated", seated);
        });

    /// <summary>Every seat has answered, so the round can be fetched.</summary>
    public static byte[] Round(int round) =>
        Wire.Json(writer =>
        {
            writer.WriteString("kind", "round");
            writer.WriteNumber("round", round);
        });

    /// <summary>The match is not being played at that pace any more.</summary>
    public static byte[] Paced(Pace pace, int windowSeconds) =>
        Wire.Json(writer =>
        {
            writer.WriteString("kind", "pace");
            writer.WriteString("pace", pace.ToString());
            writer.WriteNumber("windowSeconds", windowSeconds);
        });

    /// <summary>The text of a notice, for a test that would rather read than decode.</summary>
    public static string Read(byte[] notice) => Encoding.UTF8.GetString(notice);
}

/// <summary>
/// What has changed about a match since the last time anybody looked.
/// </summary>
/// <remarks>
/// Every decision the live hub makes is in here, and none of it touches a socket or a clock it did
/// not get handed, which is the same split the forfeit sweep uses and for the same reason: the timer
/// is dull and the rules are not.
///
/// The alternative was to ring the bell from the endpoints, at the three or four places where
/// something happens that somebody would want to know about. That is lower latency and it is one
/// forgotten call site away from a match that silently stops notifying, which is a bug that would
/// only ever show up as "sometimes it takes a second longer", the least reportable symptom there is.
/// Watching the store instead means there is one place that decides and no call site that can
/// disagree with it, and the cost is a couple of local reads per watched match per tick.
/// </remarks>
public static class LiveWatch
{
    /// <summary>
    /// How long a Live round waits on a seat that is not on a socket before the match downgrades.
    /// </summary>
    /// <remarks>
    /// Ninety seconds: the design's sixty second planning timer and half of it again. Live pace has
    /// no deadline at all, deliberately, because everybody is present and a timer would only ever
    /// fire on somebody whose phone had died. That is exactly the case this handles. A connected
    /// player always answers, since their client commits whatever they had at the buzzer, so a Live
    /// round that is still waiting is waiting on somebody who is not there.
    /// </remarks>
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(90);

    /// <summary>What a watcher remembers about a match between one look and the next.</summary>
    public readonly record struct Seen(int Round, bool Settled, int Seated, Pace Pace)
    {
        public static Seen Nothing => new Seen(0, false, -1, Pace.Live);
    }

    /// <summary>
    /// Looks at one match and says what to tell people about it.
    /// </summary>
    /// <returns>The notices to send, and what to remember for next time.</returns>
    public static (IReadOnlyList<byte[]> Notices, Seen Now) Look(
        MatchStore store, string code, Seen before)
    {
        ArgumentNullException.ThrowIfNull(store);

        if (store.Find(code) is not Match match)
        {
            return (Array.Empty<byte[]>(), before);
        }

        int seated = store.SeatsTaken(code);
        bool settled = Forfeits.Settled(
            match.PlayerCount,
            store.Submissions(code, match.Round).Count,
            store.Forfeited(code, match.Round).Count);

        List<byte[]> notices = new List<byte[]>();

        if (seated != before.Seated && before.Seated >= 0)
        {
            notices.Add(LiveNotice.Seated(seated));
        }

        if (match.Pace != before.Pace && before.Seated >= 0)
        {
            notices.Add(LiveNotice.Paced(match.Pace, match.WindowSeconds));
        }

        // Announced against the round it settles, and only once. The round number only moves when
        // somebody reads the resolved round, so between settling and being read this is true every
        // tick and must not be said every tick.
        if (settled && !(before.Settled && before.Round == match.Round))
        {
            notices.Add(LiveNotice.Round(match.Round));
        }

        return (notices, new Seen(match.Round, settled, seated, match.Pace));
    }

    /// <summary>
    /// Whether a Live match has been abandoned by somebody rather than merely being thought about.
    /// </summary>
    /// <remarks>
    /// A seat counts as gone if it has not submitted and has no socket. That does include a client
    /// that never opened one, which is the right answer rather than a loophole: Live pace's promises
    /// rest on everybody being present, and a seat the relay cannot see is a seat those promises do
    /// not cover. Anytime is where a match goes when they stop holding.
    /// </remarks>
    public static bool Abandoned(
        MatchStore store,
        string code,
        IReadOnlyCollection<int> present,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(present);

        if (store.Find(code) is not Match match
            || match.Pace != Pace.Live
            || !match.Started
            || now - match.RoundOpenedAt < Patience)
        {
            return false;
        }

        HashSet<int> answered = new HashSet<int>(
            store.Submissions(code, match.Round).Select(submission => submission.Seat));

        answered.UnionWith(store.Forfeited(code, match.Round));

        for (int seat = 0; seat < match.PlayerCount; seat++)
        {
            if (!answered.Contains(seat) && !present.Contains(seat))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// The timer that rings the bell.
/// </summary>
/// <remarks>
/// Dull on purpose, exactly like the forfeit sweeper: everything it might get wrong lives in
/// <see cref="LiveWatch"/> where it can be tested without a socket or a stopwatch. Only matches with
/// somebody actually listening are looked at, so a relay holding thousands of Anytime matches does
/// no work here at all.
/// </remarks>
public sealed partial class LiveWatcher : BackgroundService
{
    /// <summary>
    /// How often to look.
    /// </summary>
    /// <remarks>
    /// A quarter of a second. Fast enough that four phones see a round at what feels like the same
    /// moment, which is the entire point of the hub, and slow enough that the reads are nothing: two
    /// local SQLite queries per match that somebody is watching.
    /// </remarks>
    private static readonly TimeSpan Often = TimeSpan.FromMilliseconds(250);

    private readonly Dictionary<string, LiveWatch.Seen> _seen =
        new Dictionary<string, LiveWatch.Seen>(StringComparer.Ordinal);

    private readonly MatchStore _store;
    private readonly LiveHub _hub;
    private readonly TimeProvider _clock;
    private readonly ILogger<LiveWatcher> _log;

    public LiveWatcher(
        MatchStore store, LiveHub hub, TimeProvider clock, ILogger<LiveWatcher> log)
    {
        _store = store;
        _hub = hub;
        _clock = clock;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new PeriodicTimer(Often, _clock);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Look(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception trouble) when (trouble is not OperationCanceledException)
            {
                LookFailed(trouble);
            }

            try
            {
                await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One pass. Public so a test can run it without a timer.</summary>
    public async Task<int> Look(CancellationToken cancel = default)
    {
        int told = 0;
        DateTimeOffset now = _clock.GetUtcNow();
        IReadOnlyCollection<string> watched = _hub.Watched();

        foreach (string code in watched)
        {
            if (cancel.IsCancellationRequested)
            {
                break;
            }

            if (LiveWatch.Abandoned(_store, code, _hub.Present(code), now))
            {
                _store.Downgrade(code, Pace.Anytime, RoundWindow.Default);
                Downgraded(code);
            }

            LiveWatch.Seen before = _seen.TryGetValue(code, out LiveWatch.Seen was)
                ? was
                : LiveWatch.Seen.Nothing;

            (IReadOnlyList<byte[]> notices, LiveWatch.Seen state) =
                LiveWatch.Look(_store, code, before);

            // Said first, remembered second. The other order writes down that a match has been
            // told about a round and then tries to tell it, so anything that goes wrong in between
            // loses the notice rather than delaying it, and the next look sees nothing new to say.
            // It costs latency rather than correctness, since the socket is only ever a doorbell and
            // the poll underneath is the truth, but there is no reason to pay even that.
            foreach (byte[] notice in notices)
            {
                await _hub.Tell(code, notice).ConfigureAwait(false);
                told++;
            }

            _seen[code] = state;
        }

        // Matches nobody is listening to any more are forgotten, so a long-running relay does not
        // accumulate one entry per match it has ever seen. A match that comes back is a match whose
        // first look tells nobody anything, which is what a fresh listener wants anyway.
        foreach (string gone in _seen.Keys.Where(code => !watched.Contains(code)).ToArray())
        {
            _seen.Remove(gone);
        }

        return told;
    }

    [LoggerMessage(
        EventId = 20,
        Level = LogLevel.Information,
        Message = "Match {Code} lost somebody it was waiting on. Carrying on at Anytime pace.")]
    private partial void Downgraded(string code);

    [LoggerMessage(
        EventId = 21,
        Level = LogLevel.Error,
        Message = "Looking at the live matches failed. Carrying on.")]
    private partial void LookFailed(Exception trouble);
}
