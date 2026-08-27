using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Relay.Api;

/// <summary>
/// Decides what an overdue round means, without reference to a clock or a database.
/// </summary>
/// <remarks>
/// Separated from the background service on purpose. The service is a timer and a loop, which is
/// tedious to test and holds no decisions; this holds every decision and is trivial to test. What
/// counts as overdue, who forfeits, and whether a round can resolve are the parts worth being sure
/// about, and none of them need a stopwatch.
/// </remarks>
public static class Forfeits
{
    /// <summary>
    /// Sweeps one match: forfeits whoever ran out of window, and says whether anything changed.
    /// </summary>
    /// <remarks>
    /// A seat that has already submitted is never forfeited, even long past the deadline, because
    /// their turn is in and taking it away would be worse than the delay. The forfeit is for the
    /// seats that are simply not coming.
    ///
    /// One player who loses interest must not be able to end a match for three other people by never
    /// opening the game again. That is the whole reason this exists, and it is why the sweep is the
    /// relay's job rather than a client's: the client that would notice is the one that is waiting.
    /// </remarks>
    public static int Sweep(MatchStore store, string code, DateTimeOffset now)
    {
        if (store.Find(code) is not Match match || !match.Started || !match.Expired(now))
        {
            return 0;
        }

        HashSet<int> answered = new HashSet<int>(
            store.Submissions(code, match.Round).Select(submission => submission.Seat));

        answered.UnionWith(store.Forfeited(code, match.Round));

        int forfeited = 0;

        for (int seat = 0; seat < match.PlayerCount; seat++)
        {
            if (answered.Contains(seat))
            {
                continue;
            }

            if (store.Forfeit(code, match.Round, seat, now))
            {
                forfeited++;
            }
        }

        return forfeited;
    }

    /// <summary>
    /// Whether every seat has either committed or forfeited, which is when a round can be released.
    /// </summary>
    /// <remarks>
    /// The rule that used to be "every seat has submitted" and cannot stay that way, or an Anytime
    /// match with one absent player would wait for a plan that is never coming and the forfeit would
    /// achieve nothing.
    /// </remarks>
    public static bool Settled(int playerCount, int submitted, int forfeited) =>
        submitted + forfeited >= playerCount;
}

/// <summary>
/// The timer that runs the sweep.
/// </summary>
/// <remarks>
/// Deliberately dull, and deliberately holding no decisions: everything it might get wrong lives in
/// <see cref="Forfeits"/> where it can be tested without waiting for a clock.
///
/// One process sweeping is fine at this scale, and the forfeit insert is idempotent, so two of them
/// racing would produce the same answer twice rather than two different answers.
/// </remarks>
public sealed partial class ForfeitSweeper : BackgroundService
{
    /// <summary>
    /// How often to look.
    /// </summary>
    /// <remarks>
    /// Half a minute, which is far finer than a day-long window needs and is chosen for the short
    /// windows a playtest uses: a host who sets the minimum minute should not wait another five to
    /// see the forfeit land. It is a cheap query against an index of started matches.
    /// </remarks>
    private static readonly TimeSpan Often = TimeSpan.FromSeconds(30);

    private readonly MatchStore _store;
    private readonly TimeProvider _clock;
    private readonly ILogger<ForfeitSweeper> _log;

    public ForfeitSweeper(MatchStore store, TimeProvider clock, ILogger<ForfeitSweeper> log)
    {
        _store = store;
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
                Round(_clock.GetUtcNow());
            }
            catch (Exception trouble) when (trouble is not OperationCanceledException)
            {
                // A sweep that throws must not take the relay down with it, and must not stop
                // sweeping either: whatever went wrong for one match, the others still have
                // deadlines.
                SweepFailed(trouble);
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

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Error,
        Message = "A forfeit sweep failed. Carrying on.")]
    private partial void SweepFailed(Exception trouble);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Information,
        Message = "Match {Code} forfeited {Seats} seat(s) at the end of its window.")]
    private partial void Forfeited(string code, int seats);

    /// <summary>One pass. Public so a test can run it without a timer.</summary>
    public int Round(DateTimeOffset now)
    {
        int forfeited = 0;

        foreach (string code in _store.Overdue(now))
        {
            int gaveUp = Forfeits.Sweep(_store, code, now);

            if (gaveUp > 0)
            {
                Forfeited(code, gaveUp);
            }

            forfeited += gaveUp;
        }

        return forfeited;
    }
}
