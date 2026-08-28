namespace Relay.Api;

/// <summary>A device that wants telling when its turn comes round.</summary>
public sealed record Device(string Code, int Seat, string Token, string Platform, DateTimeOffset At);

/// <summary>One notification the relay has decided to send but has not sent.</summary>
public sealed record Nudge(
    string Code, int Seat, int Round, string DeviceToken, DateTimeOffset DecidedAt);

/// <summary>
/// Who should be told that a round resolved, and whether they may be told yet.
/// </summary>
/// <remarks>
/// Decided here and sent elsewhere, deliberately. The decision is the part with a rule in it and it
/// is worth being certain about; the sending is an HTTP call to somebody else's service. Splitting
/// them means the rule is tested without a Firebase project in the loop, an undeliverable
/// notification is a row rather than a lost event, and swapping the sender is one class.
///
/// The rule the design fixes is "one notification a day per match at most". That is a real
/// constraint rather than a nicety: an Anytime match runs for a fortnight, four players are in it,
/// and a game that pushes on every resolution is a game people mute and then stop playing. It also
/// means a player can genuinely miss knowing it is their turn, which is the trade the design accepts
/// and the reason the window in the game is a day rather than an hour.
/// </remarks>
public static class Nudges
{
    /// <summary>
    /// How long a device is left alone after being told about a match.
    /// </summary>
    /// <remarks>
    /// Per match and per seat rather than per device, so somebody playing three matches at once still
    /// hears about all three. It is one match being noisy that the rule is about.
    /// </remarks>
    public static readonly TimeSpan Quiet = TimeSpan.FromHours(24);

    /// <summary>
    /// Decides who to tell about a round, and records the decision.
    /// </summary>
    /// <remarks>
    /// Only seats with a device registered, only seats other than the ones already told recently, and
    /// never the seat that has already committed for the round being announced: telling somebody it is
    /// their turn when they have already taken it is the fastest way to teach them to ignore the
    /// notifications.
    /// </remarks>
    public static IReadOnlyList<Nudge> Decide(
        MatchStore store, string code, int round, DateTimeOffset now)
    {
        if (store.Find(code) is not Match match || !match.Started)
        {
            return Array.Empty<Nudge>();
        }

        HashSet<int> alreadyIn = new HashSet<int>(
            store.Submissions(code, round).Select(submission => submission.Seat));

        List<Nudge> decided = new List<Nudge>();

        foreach (Device device in store.Devices(code))
        {
            if (alreadyIn.Contains(device.Seat))
            {
                continue;
            }

            if (store.LastNudged(code, device.Seat) is DateTimeOffset last
                && now - last < Quiet)
            {
                continue;
            }

            Nudge nudge = new Nudge(code, device.Seat, round, device.Token, now);

            if (store.RecordNudge(nudge))
            {
                decided.Add(nudge);
            }
        }

        return decided;
    }
}

/// <summary>
/// What happened to a nudge, and therefore what to do with it next.
/// </summary>
/// <remarks>
/// This used to be a bool, and the bool was going to start lying. "Sent" and "stop trying" are the
/// same instruction to the outbox and different facts about the world, and a notification service
/// refuses in at least three ways that want different answers: come back later, never mind, and that
/// phone is gone. A yes-or-no forces two of those three to be wrong, and the wrong one is expensive
/// either way, since an outbox that retries a dead phone spins for ever and one that gives up on a
/// busy service loses the round.
/// </remarks>
public enum Delivery
{
    /// <summary>Accepted by the far end. Done with.</summary>
    Sent = 0,

    /// <summary>Nothing wrong with it, the far end is just not answering. Leave it pending.</summary>
    Deferred = 1,

    /// <summary>Refused for good. Stop trying, but the device is fine.</summary>
    Dropped = 2,

    /// <summary>That phone is gone. Stop trying, and forget where it was.</summary>
    Unregistered = 3,
}

/// <summary>
/// Where a decided notification actually goes.
/// </summary>
/// <remarks>
/// An interface with one method, because there is exactly one thing to do with a nudge and two
/// places to do it: a log, during development and in the tests, or Firebase Cloud Messaging, in the
/// field. See <see cref="FirebaseNudgeSender"/> for the second one, and for what it can and cannot
/// claim without a Firebase project to send to.
/// </remarks>
public interface INudgeSender
{
    Task<Delivery> Send(Nudge nudge, CancellationToken cancel = default);
}

/// <summary>
/// The sender used until there is a Firebase project: it writes the nudge down and calls it sent.
/// </summary>
/// <remarks>
/// Not a stub that throws, and not one that silently does nothing either. During development the
/// interesting question is whether the right people are being told at the right times, and a log line
/// answers it exactly as well as a phone buzzing would.
/// </remarks>
public sealed partial class LoggingNudgeSender : INudgeSender
{
    private readonly ILogger<LoggingNudgeSender> _log;

    public LoggingNudgeSender(ILogger<LoggingNudgeSender> log) => _log = log;

    public Task<Delivery> Send(Nudge nudge, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(nudge);

        Nudged(nudge.Code, nudge.Seat, nudge.Round);

        return Task.FromResult(Delivery.Sent);
    }

    [LoggerMessage(
        EventId = 10,
        Level = LogLevel.Information,
        Message = "Would tell match {Code} seat {Seat} that round {Round} is waiting for them.")]
    private partial void Nudged(string code, int seat, int round);
}

/// <summary>
/// Empties the outbox.
/// </summary>
/// <remarks>
/// Separate from deciding, so a notification service being down delays a buzz instead of failing a
/// round read. A nudge that cannot be delivered stays pending and is tried again, and one that is
/// delivered is marked so nothing sends it twice.
/// </remarks>
public sealed partial class NudgeDrain : BackgroundService
{
    /// <summary>
    /// How often to look.
    /// </summary>
    /// <remarks>
    /// Five seconds. A player who has just been given their turn does not need to hear about it
    /// instantly, and in Anytime pace the round they are being told about has hours left on it.
    /// </remarks>
    private static readonly TimeSpan Often = TimeSpan.FromSeconds(5);

    private readonly MatchStore _store;
    private readonly INudgeSender _sender;
    private readonly TimeProvider _clock;
    private readonly ILogger<NudgeDrain> _log;

    public NudgeDrain(
        MatchStore store, INudgeSender sender, TimeProvider clock, ILogger<NudgeDrain> log)
    {
        _store = store;
        _sender = sender;
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
                await Drain(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception trouble) when (trouble is not OperationCanceledException)
            {
                DrainFailed(trouble);
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
    public async Task<int> Drain(CancellationToken cancel = default)
    {
        int sent = 0;

        foreach (Nudge nudge in _store.PendingNudges())
        {
            Delivery answer = await _sender.Send(nudge, cancel).ConfigureAwait(false);

            if (answer == Delivery.Deferred)
            {
                continue;
            }

            if (answer == Delivery.Unregistered)
            {
                // Only if it is still the token on file. A player who reinstalled between the nudge
                // being decided and this failing has a good device registered by now, and deleting
                // it because the old one is dead would mean never reaching them again.
                _store.ForgetDevice(nudge.Code, nudge.Seat, nudge.DeviceToken);
            }

            // Marked done for all three of the answers that are not "later", including the two that
            // were never delivered. The column says sent and means finished with, which is the only
            // thing the outbox needs from it: a nudge nobody can receive is not worth carrying, and
            // the next round decides a fresh one anyway.
            _store.NudgeSent(nudge.Code, nudge.Seat, nudge.Round);

            if (answer == Delivery.Sent)
            {
                sent++;
            }
        }

        return sent;
    }

    [LoggerMessage(
        EventId = 11,
        Level = LogLevel.Error,
        Message = "Draining the notification outbox failed. Carrying on.")]
    private partial void DrainFailed(Exception trouble);
}
