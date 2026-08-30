using Relay.Api;

namespace Relay.Tests;

/// <summary>
/// The two places where a plausible sequence of events produced a state no client can survive.
/// </summary>
/// <remarks>
/// Neither of these is a knife-edge race, which is why they are worth tests rather than comments.
/// One is opened by a slow upload, which on a phone is the normal case rather than the exception;
/// the other by somebody pressing cancel, which is a button. Both used to end with a match that
/// every participant had to abandon.
/// </remarks>
[TestFixture]
public sealed class RaceTests
{
    private MatchStore _store = null!;
    private DateTimeOffset _now;

    [SetUp]
    public void SetUp()
    {
        _store = MatchStore.InMemory($"race-{TestContext.CurrentContext.Test.ID}");
        _now = new DateTimeOffset(2026, 8, 31, 9, 0, 0, TimeSpan.Zero);
    }

    [TearDown]
    public void TearDown() => _store.Dispose();

    // ---- A plan and a forfeit for the same seat ------------------------------------------

    /// <summary>
    /// The sequence: a deadline passes, the sweep forfeits a seat that has not answered, and that
    /// seat's plan arrives a moment later because it was still uploading. Both rows used to land,
    /// and a round released with a seat that had both submitted and forfeited is a contradiction
    /// every client resolves by giving up on the match.
    /// </summary>
    [Test]
    public void APlanThatArrivesAfterTheForfeitIsRefused()
    {
        Match match = Started(2);

        Assert.That(_store.Forfeit(match.Code, 1, 1, _now), Is.True);

        Assert.That(
            _store.Submit(match.Code, 1, 1, new byte[] { 9 }, _now), Is.False,
            "A forfeited seat must not also be able to submit.");

        Assert.That(_store.Submissions(match.Code, 1), Is.Empty);
    }

    /// <summary>And the other order, since the sweep and the upload can land either way round.</summary>
    [Test]
    public void ASeatThatSubmittedIsStillForfeitableOnlyOnceAndNeverBoth()
    {
        Match match = Started(2);

        Assert.That(_store.Submit(match.Code, 1, 1, new byte[] { 9 }, _now), Is.True);

        // The sweep does not forfeit a seat it can see has answered, and Forfeits.Sweep reads the
        // submissions first for exactly that reason. What matters here is that even if it did, the
        // release cannot contain both: whichever arrived first owns the seat.
        _store.Forfeit(match.Code, 1, 1, _now);

        bool submitted = _store.Submissions(match.Code, 1).Any(one => one.Seat == 1);
        bool forfeited = _store.Forfeited(match.Code, 1).Contains(1);

        Assert.That(
            submitted && forfeited, Is.False,
            "One seat, one answer. A release carrying both ends the match for everybody.");
    }

    /// <summary>
    /// A plan for a round the match has moved past is refused too. The endpoint checks the round
    /// before it awaits the request body, so a slow upload can be aimed at a round that finished
    /// while it was in flight.
    /// </summary>
    [Test]
    public void APlanForARoundTheMatchHasLeftIsRefused()
    {
        Match match = Started(2);

        _store.Advance(match.Code, 2, _now);

        Assert.That(_store.Submit(match.Code, 1, 0, new byte[] { 9 }, _now), Is.False);
    }

    // ---- A seat nobody is coming to ------------------------------------------------------

    /// <summary>
    /// Somebody gives up between the pool choosing a group and seating it. The seat used to be
    /// claimed anyway and its token thrown away, leaving the others in a match with a participant
    /// who was never coming: Live waited out the ninety second downgrade, Anytime blocked the round
    /// for a day.
    /// </summary>
    [Test]
    public void AGroupWithSomebodyMissingSeatsNobody()
    {
        Ticket first = Queued();
        Ticket second = Queued();

        _store.LeaveQueue(second.Id);

        Assert.That(
            _store.SeatGroup(new[] { first.Id, second.Id }, 2, Pace.Live, _now), Is.False);

        // And the one who stayed is still waiting, rather than sitting in a lobby on their own.
        Ticket? left = _store.Held(first.Id);

        Assert.That(left, Is.Not.Null);
        Assert.That(left!.Seated, Is.False);
    }

    [Test]
    public void AGroupThatIsAllStillThereSeatsEverybody()
    {
        Ticket first = Queued();
        Ticket second = Queued();

        Assert.That(
            _store.SeatGroup(new[] { first.Id, second.Id }, 2, Pace.Live, _now), Is.True);

        Ticket one = _store.Held(first.Id)!;
        Ticket two = _store.Held(second.Id)!;

        Assert.That(one.Seated, Is.True);
        Assert.That(two.Seated, Is.True);
        Assert.That(one.Code, Is.EqualTo(two.Code));
        Assert.That(one.Seat, Is.Not.EqualTo(two.Seat));
        Assert.That(_store.SeatsTaken(one.Code!), Is.EqualTo(2));
    }

    /// <summary>
    /// Seating a group twice does not put anybody in a second lobby, so a pass of the pool that
    /// overlapped another cannot move a player out of the match they were already given.
    /// </summary>
    [Test]
    public void AGroupAlreadySeatedIsNotSeatedAgain()
    {
        Ticket first = Queued();
        Ticket second = Queued();

        _store.SeatGroup(new[] { first.Id, second.Id }, 2, Pace.Live, _now);

        string? landed = _store.Held(first.Id)!.Code;

        Assert.That(
            _store.SeatGroup(new[] { first.Id, second.Id }, 2, Pace.Live, _now), Is.False);
        Assert.That(_store.Held(first.Id)!.Code, Is.EqualTo(landed));
    }

    // ---- Helpers ------------------------------------------------------------------------

    private Match Started(int players)
    {
        (Match opened, Seat _) = _store.Open(players, Pace.Anytime, _now);

        for (int seat = 1; seat < players; seat++)
        {
            _store.Join(opened.Code, _now);
        }

        return _store.Find(opened.Code)!;
    }

    private Ticket Queued()
    {
        (Account account, string _) = _store.OpenAccount(AgeBand.Adult, _now);

        return _store.JoinQueue(account.Id, 2, Pace.Live, _now);
    }
}
