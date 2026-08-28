using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Relay.Api;

/// <summary>
/// Somebody in the pool, waiting to be put in a match.
/// </summary>
/// <remarks>
/// The ticket rather than the account is what the client comes back with, so a player can be asked
/// about their own place in the queue without handing their account credential over on every poll.
/// Once the pool has seated them, the seat arrives on the same ticket: one thing to hold and one
/// thing to ask about, from pressing the button to standing in the lobby.
/// </remarks>
public sealed record Ticket(
    string Id,
    string Account,
    int PlayerCount,
    Pace Pace,
    DateTimeOffset JoinedAt,
    string? Code,
    int Seat,
    string? SeatToken)
{
    /// <summary>Whether the pool has found this one a game yet.</summary>
    public bool Seated => Code is not null;
}

/// <summary>
/// The pool: who goes with whom, and when to admit the queue is not filling.
/// </summary>
/// <remarks>
/// One region-wide pool, no skill brackets, no ranking, exactly as the design says and for the reason
/// it gives: a new free game with a thin population is a queue that never fills, and every bracket
/// divides a population that cannot afford dividing. The only thing that separates one queue from
/// another is how many seats somebody asked for, because "come play a duel" and "come make up a four"
/// are different requests rather than different skill levels.
///
/// Everything here is a pure function of who is waiting, so the interesting parts can be tested
/// without a clock or a database: who gets matched, in what order, and when somebody has waited long
/// enough to be told the truth about it.
/// </remarks>
public static class Matchmaker
{
    /// <summary>
    /// How long somebody waits before the game admits the pool is thin.
    /// </summary>
    /// <remarks>
    /// Forty-five seconds. The design's answer to an empty pool is not a spinner with a better
    /// animation, it is Anytime pace, which "needs no concurrency at all" and is offered by default
    /// to anyone whose Live queue is slow. This is when that offer is made, and it is deliberately
    /// short: a player who has been staring at a queue for a minute has already decided the game is
    /// dead, and the whole point is to reach them before they do.
    /// </remarks>
    public static readonly TimeSpan Slow = TimeSpan.FromSeconds(45);

    /// <summary>
    /// Cuts the queue into full matches, oldest first.
    /// </summary>
    /// <remarks>
    /// Oldest first is the only fairness rule in here and it is worth stating: nobody is ever put in
    /// a match ahead of somebody who was already waiting for the same size, so a queue that is
    /// filling slowly still empties in the order it filled. Without it, a pool that receives players
    /// faster than it can seat them would leave whoever arrived first waiting the longest, which is
    /// precisely backwards.
    ///
    /// Partial groups are left alone rather than seated short. A lobby is opened for a player count
    /// somebody asked for, and three people who wanted a four are still waiting for a four: the host
    /// of a coded lobby may lower the count and start, because a host is a person making a decision,
    /// but nothing here is in a position to decide that on anybody's behalf.
    /// </remarks>
    public static IReadOnlyList<IReadOnlyList<Ticket>> Pair(
        IReadOnlyList<Ticket> waiting, int playerCount)
    {
        ArgumentNullException.ThrowIfNull(waiting);

        List<IReadOnlyList<Ticket>> matches = new List<IReadOnlyList<Ticket>>();

        if (playerCount is < 2 or > 4)
        {
            return matches;
        }

        List<Ticket> queue = waiting
            .Where(ticket => !ticket.Seated && ticket.PlayerCount == playerCount)
            .OrderBy(ticket => ticket.JoinedAt)
            .ThenBy(ticket => ticket.Id, StringComparer.Ordinal)
            .ToList();

        for (int from = 0; from + playerCount <= queue.Count; from += playerCount)
        {
            matches.Add(queue.GetRange(from, playerCount));
        }

        return matches;
    }

    /// <summary>Whether somebody has waited long enough to be offered the other pace.</summary>
    public static bool Slowly(Ticket ticket, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        return !ticket.Seated && ticket.Pace == Pace.Live && now - ticket.JoinedAt >= Slow;
    }

    /// <summary>
    /// Fills every match the queue can currently make, and seats the people in them.
    /// </summary>
    /// <remarks>
    /// The lobby is opened the ordinary way, through the same code path a host uses, so a matchmade
    /// match is an ordinary match in every respect from the moment it exists. It has a code somebody
    /// could read out, it seats people through Join, and nothing downstream can tell how its players
    /// found each other. That is worth more than it costs: every rule about rounds, forfeits,
    /// notifications and sockets already works on it, because there is nothing new to work on.
    ///
    /// The first ticket in a group becomes the host's seat purely because somebody has to be seat
    /// zero. Nothing about hosting means anything here, since the count and the pace were decided by
    /// whoever joined the queue rather than by a person in a lobby.
    /// </remarks>
    public static int Fill(MatchStore store, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(store);

        int made = 0;
        IReadOnlyList<Ticket> waiting = store.Queue();

        for (int playerCount = 2; playerCount <= 4; playerCount++)
        {
            foreach (Pace pace in new[] { Pace.Live, Pace.Anytime })
            {
                IReadOnlyList<Ticket> paced = waiting
                    .Where(ticket => ticket.Pace == pace)
                    .ToArray();

                foreach (IReadOnlyList<Ticket> group in Pair(paced, playerCount))
                {
                    Seat(store, group, pace, now);
                    made++;
                }
            }
        }

        return made;
    }

    private static void Seat(
        MatchStore store, IReadOnlyList<Ticket> group, Pace pace, DateTimeOffset now)
    {
        (Match match, Seat host) = store.Open(group.Count, pace, now);

        store.Seated(group[0].Id, match.Code, host.Number, host.Token);

        for (int index = 1; index < group.Count; index++)
        {
            (Seat? seat, JoinRefusal refusal) = store.Join(match.Code, now);

            if (seat is null || refusal != JoinRefusal.None)
            {
                // Cannot happen with a lobby this service opened a moment ago for exactly this many
                // people, and if it ever does, the answer is to leave the ticket in the queue rather
                // than to strand somebody in a lobby nobody else is coming to.
                return;
            }

            store.Seated(group[index].Id, match.Code, seat.Number, seat.Token);
        }
    }
}

/// <summary>
/// The timer that empties the pool.
/// </summary>
/// <remarks>
/// Dull on purpose, like the forfeit sweeper and the live watcher, and holding no decisions for the
/// same reason: everything it might get wrong lives in <see cref="Matchmaker"/> where it can be
/// tested without waiting for anything.
/// </remarks>
public sealed partial class PoolFiller : BackgroundService
{
    /// <summary>
    /// How often to look.
    /// </summary>
    /// <remarks>
    /// A second. Nobody notices the difference between being matched instantly and being matched a
    /// second later, and the sweep is one query against a table that is empty most of the time.
    /// </remarks>
    private static readonly TimeSpan Often = TimeSpan.FromSeconds(1);

    private readonly MatchStore _store;
    private readonly TimeProvider _clock;
    private readonly ILogger<PoolFiller> _log;

    public PoolFiller(MatchStore store, TimeProvider clock, ILogger<PoolFiller> log)
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
                int made = Matchmaker.Fill(_store, _clock.GetUtcNow());

                if (made > 0)
                {
                    Filled(made);
                }
            }
            catch (Exception trouble) when (trouble is not OperationCanceledException)
            {
                FillFailed(trouble);
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
        EventId = 30,
        Level = LogLevel.Information,
        Message = "Made {Made} match(es) out of the pool.")]
    private partial void Filled(int made);

    [LoggerMessage(
        EventId = 31,
        Level = LogLevel.Error,
        Message = "Filling matches out of the pool failed. Carrying on.")]
    private partial void FillFailed(Exception trouble);
}
