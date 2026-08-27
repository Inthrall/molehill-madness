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
/// Where a decided notification actually goes.
/// </summary>
/// <remarks>
/// An interface with one method, because there is exactly one thing to do with a nudge and two
/// plausible places to do it: a log, during development and in the tests, or Firebase Cloud
/// Messaging, in the field.
///
/// The Firebase implementation is not here. FCM's v1 API needs a service account key and an OAuth2
/// bearer minted from it, and there is no Firebase project to point it at yet, so writing the sender
/// now would mean committing code that looks finished and has never delivered a message. The queue is
/// the useful half and it is complete: nudges are decided, throttled and recorded, and whatever
/// drains them is a later and smaller problem than getting the rule right.
/// </remarks>
public interface INudgeSender
{
    Task<bool> Send(Nudge nudge, CancellationToken cancel = default);
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

    public Task<bool> Send(Nudge nudge, CancellationToken cancel = default)
    {
        Nudged(nudge.Code, nudge.Seat, nudge.Round);

        return Task.FromResult(true);
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
            if (await _sender.Send(nudge, cancel).ConfigureAwait(false))
            {
                _store.NudgeSent(nudge.Code, nudge.Seat, nudge.Round);
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
