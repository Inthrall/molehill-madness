using Relay.Api;

namespace Relay.Tests;

/// <summary>
/// Round windows and forfeits: what stops one absent player ending a match for everybody else.
/// </summary>
/// <remarks>
/// Every one of these passes a time in rather than reading a clock, which is the whole reason the
/// decisions were separated from the background service that runs them. A day-long window is a
/// perfectly ordinary thing to test when the day is an argument.
/// </remarks>
[TestFixture]
public sealed class ForfeitTests
{
    private MatchStore _store = null!;
    private DateTimeOffset _opened;

    [SetUp]
    public void SetUp()
    {
        _store = MatchStore.InMemory($"forfeit-{TestContext.CurrentContext.Test.ID}");
        _opened = new DateTimeOffset(2026, 8, 28, 9, 0, 0, TimeSpan.Zero);
    }

    [TearDown]
    public void TearDown() => _store.Dispose();

    // ---- Windows ------------------------------------------------------------------------

    [Test]
    public void LivePaceHasNoWindowAtAll()
    {
        (Match match, Seat _) = _store.Open(2, Pace.Live, _opened, windowSeconds: 3600);

        Assert.That(match.WindowSeconds, Is.EqualTo(0));
        Assert.That(match.Deadline, Is.Null, "Everybody is present, so nobody can run out of time.");
        Assert.That(match.Expired(_opened.AddYears(1)), Is.False);
    }

    [Test]
    public void AnytimeTakesTheDesignsDayByDefault()
    {
        (Match match, Seat _) = _store.Open(2, Pace.Anytime, _opened);

        Assert.That(match.WindowSeconds, Is.EqualTo(RoundWindow.Default));
    }

    [TestCase(1, RoundWindow.Shortest)]
    [TestCase(30, RoundWindow.Shortest)]
    [TestCase(99999999, RoundWindow.Longest)]
    public void AWindowIsClampedToSomethingSane(int asked, int expected)
    {
        (Match match, Seat _) = _store.Open(2, Pace.Anytime, _opened, asked);

        Assert.That(match.WindowSeconds, Is.EqualTo(expected));
    }

    /// <summary>
    /// A host who sets a one-hour window and then spends an hour finding a fourth player must not
    /// have the first round forfeit itself the moment it begins.
    /// </summary>
    [Test]
    public void RoundOnesWindowStartsWhenTheLobbyFillsNotWhenItOpened()
    {
        (Match opened, Seat _) = _store.Open(2, Pace.Anytime, _opened, windowSeconds: 3600);

        DateTimeOffset muchLater = _opened.AddHours(5);
        _store.Join(opened.Code, muchLater);

        Match started = _store.Find(opened.Code)!;

        Assert.That(started.RoundOpenedAt, Is.EqualTo(muchLater.ToUniversalTime()));
        Assert.That(started.Expired(muchLater.AddMinutes(1)), Is.False);
        Assert.That(started.Expired(muchLater.AddMinutes(61)), Is.True);
    }

    [Test]
    public void EachRoundGetsItsOwnFullWindow()
    {
        Match match = Started(2, windowSeconds: 3600, out _, out _);

        DateTimeOffset resolved = _opened.AddMinutes(50);
        _store.Advance(match.Code, 2, resolved);

        Match next = _store.Find(match.Code)!;

        Assert.That(next.Round, Is.EqualTo(2));
        Assert.That(next.Expired(resolved.AddMinutes(30)), Is.False, "Round two inherited round one's clock.");
        Assert.That(next.Expired(resolved.AddMinutes(61)), Is.True);
    }

    // ---- Sweeping -----------------------------------------------------------------------

    [Test]
    public void NobodyForfeitsBeforeTheDeadline()
    {
        Match match = Started(2, windowSeconds: 3600, out _, out _);

        Assert.That(Forfeits.Sweep(_store, match.Code, _opened.AddMinutes(59)), Is.EqualTo(0));
        Assert.That(_store.Forfeited(match.Code, 1), Is.Empty);
    }

    [Test]
    public void AfterTheDeadlineTheSeatsThatDidNothingForfeit()
    {
        Match match = Started(3, windowSeconds: 3600, out Seat host, out _);

        _store.Submit(match.Code, 1, host.Number, new byte[] { 1 }, _opened);

        int gaveUp = Forfeits.Sweep(_store, match.Code, _opened.AddHours(2));

        Assert.That(gaveUp, Is.EqualTo(2));
        Assert.That(_store.Forfeited(match.Code, 1), Is.EqualTo(new[] { 1, 2 }));
    }

    /// <summary>
    /// A seat that got its turn in keeps it, however late the sweep runs. Taking a submitted plan
    /// away would be worse than the delay it was late by.
    /// </summary>
    [Test]
    public void ASeatThatCommittedIsNeverForfeited()
    {
        Match match = Started(2, windowSeconds: 60, out Seat host, out _);

        _store.Submit(match.Code, 1, host.Number, new byte[] { 1 }, _opened);
        Forfeits.Sweep(_store, match.Code, _opened.AddDays(30));

        Assert.That(_store.Forfeited(match.Code, 1), Is.EqualTo(new[] { 1 }));
        Assert.That(_store.Submissions(match.Code, 1), Has.Count.EqualTo(1));
    }

    /// <summary>
    /// The sweep runs on a timer and from every round read, so it happens repeatedly and must be
    /// safe to. A second pass must not double-count or change anything.
    /// </summary>
    [Test]
    public void SweepingTwiceForfeitsNothingNew()
    {
        Match match = Started(2, windowSeconds: 60, out _, out _);

        int first = Forfeits.Sweep(_store, match.Code, _opened.AddHours(1));
        int again = Forfeits.Sweep(_store, match.Code, _opened.AddHours(1));

        Assert.That(first, Is.EqualTo(2));
        Assert.That(again, Is.EqualTo(0));
        Assert.That(_store.Forfeited(match.Code, 1), Is.EqualTo(new[] { 0, 1 }));
    }

    [Test]
    public void ALobbyStillFillingNeverForfeits()
    {
        (Match match, Seat _) = _store.Open(3, Pace.Anytime, _opened, windowSeconds: 60);

        Assert.That(
            Forfeits.Sweep(_store, match.Code, _opened.AddDays(1)),
            Is.EqualTo(0),
            "A match that has not started cannot have missed a turn.");
    }

    [Test]
    public void ALivePaceMatchNeverForfeits()
    {
        Match match = Started(2, windowSeconds: 0, out _, out _, Pace.Live);

        Assert.That(Forfeits.Sweep(_store, match.Code, _opened.AddYears(1)), Is.EqualTo(0));
    }

    // ---- Finding the overdue ------------------------------------------------------------

    [Test]
    public void OnlyStartedAnytimeMatchesPastTheirWindowAreOverdue()
    {
        Match overdue = Started(2, windowSeconds: 60, out _, out _);
        Match inTime = Started(2, windowSeconds: 86400, out _, out _);
        Match live = Started(2, windowSeconds: 0, out _, out _, Pace.Live);
        (Match filling, Seat _) = _store.Open(3, Pace.Anytime, _opened, windowSeconds: 60);

        IReadOnlyList<string> found = _store.Overdue(_opened.AddHours(1));

        Assert.That(found, Does.Contain(overdue.Code));
        Assert.That(found, Does.Not.Contain(inTime.Code), "Its window has hours left.");
        Assert.That(found, Does.Not.Contain(live.Code), "Live pace has no window to run out of.");
        Assert.That(found, Does.Not.Contain(filling.Code), "Never started.");
    }

    /// <summary>
    /// The sweeper drives itself off Overdue, so this is the pair working together rather than
    /// either alone. It is also the one test that touches the background service at all.
    /// </summary>
    [Test]
    public void TheSweeperClearsEverythingOverdueInOnePass()
    {
        Match first = Started(2, windowSeconds: 60, out _, out _);
        Match second = Started(3, windowSeconds: 60, out _, out _);

        ForfeitSweeper sweeper = new ForfeitSweeper(
            _store,
            TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ForfeitSweeper>.Instance);

        int gaveUp = sweeper.Round(_opened.AddHours(1));

        Assert.That(gaveUp, Is.EqualTo(5));
        Assert.That(_store.Forfeited(first.Code, 1), Has.Count.EqualTo(2));
        Assert.That(_store.Forfeited(second.Code, 1), Has.Count.EqualTo(3));
    }

    // ---- Settling -----------------------------------------------------------------------

    /// <summary>
    /// The rule that had to change. "Every seat has submitted" would leave an Anytime match waiting
    /// forever on a plan that is not coming, and the forfeit would achieve nothing at all.
    /// </summary>
    [TestCase(2, 2, 0, true)]
    [TestCase(2, 1, 1, true)]
    [TestCase(2, 0, 2, true)]
    [TestCase(2, 1, 0, false)]
    [TestCase(4, 2, 1, false)]
    [TestCase(4, 2, 2, true)]
    public void ARoundSettlesOnceEverySeatHasCommittedOrForfeited(
        int players, int submitted, int forfeited, bool settled)
    {
        Assert.That(Forfeits.Settled(players, submitted, forfeited), Is.EqualTo(settled));
    }

    // ---- Helpers ------------------------------------------------------------------------

    /// <summary>A match with every seat taken, so its window is running.</summary>
    private Match Started(
        int players, int windowSeconds, out Seat host, out Seat last, Pace pace = Pace.Anytime)
    {
        (Match opened, Seat first) = _store.Open(players, pace, _opened, windowSeconds);
        host = first;
        last = first;

        for (int seat = 1; seat < players; seat++)
        {
            (Seat? joined, JoinRefusal _) = _store.Join(opened.Code, _opened);
            last = joined!;
        }

        return _store.Find(opened.Code)!;
    }
}
