using Relay.Api;

namespace Relay.Tests;

/// <summary>
/// The store, against real SQLite.
/// </summary>
/// <remarks>
/// In memory but not in effigy: this is the same SQL, the same types and the same primary keys the
/// deployed relay runs, because the interesting behaviour here lives in the schema rather than in
/// the C#. "First submission wins" is an INSERT OR IGNORE against a composite primary key, and a
/// substitute store would happily agree with a bug in it.
/// </remarks>
[TestFixture]
public sealed class MatchStoreTests
{
    private MatchStore _store = null!;
    private DateTimeOffset _now;

    [SetUp]
    public void SetUp()
    {
        // A database per test, so nothing leaks between them.
        _store = MatchStore.InMemory($"store-{TestContext.CurrentContext.Test.ID}");
        _now = new DateTimeOffset(2026, 8, 27, 9, 30, 0, TimeSpan.FromHours(12));
    }

    [TearDown]
    public void TearDown() => _store.Dispose();

    // ---- Opening ------------------------------------------------------------------------

    [Test]
    public void OpeningALobbySeatsTheHostFirst()
    {
        (Match match, Seat host) = _store.Open(playerCount: 3, Pace.Live, _now);

        Assert.That(host.Number, Is.EqualTo(0));
        Assert.That(host.Code, Is.EqualTo(match.Code));
        Assert.That(host.Token, Is.Not.Empty);
        Assert.That(_store.SeatsTaken(match.Code), Is.EqualTo(1));
        Assert.That(match.Round, Is.EqualTo(1));
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(5)]
    public void AMatchIsTwoToFourPlayers(int playerCount)
    {
        Assert.That(
            () => _store.Open(playerCount, Pace.Live, _now),
            Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void AMatchReadsBackAsItWasOpened()
    {
        (Match opened, Seat _) = _store.Open(playerCount: 4, Pace.Anytime, _now);

        Match? found = _store.Find(opened.Code);

        Assert.That(found, Is.Not.Null);
        Assert.That(found!.PlayerCount, Is.EqualTo(4));
        Assert.That(found.Pace, Is.EqualTo(Pace.Anytime));
        Assert.That(found.Round, Is.EqualTo(1));
        Assert.That(found.OpenedAt, Is.EqualTo(_now.ToUniversalTime()));
    }

    /// <summary>
    /// The seed is the whole shared world. Every client grows the same map, the same wind and the
    /// same scatter out of it, so a seed that comes back off disk even slightly wrong is a match
    /// where four players are fighting over four different battlefields.
    /// </summary>
    /// <remarks>
    /// Stored as TEXT, which is why this is worth a test rather than an assumption: it is the one
    /// value in the store that goes through a number-to-string-to-number round trip, and a
    /// culture that groups digits or a signed read of a value past long.MaxValue would both corrupt
    /// it silently. The assertion on how many seeds had the top bit set is there so the test cannot
    /// pass while quietly never exercising the case it exists for.
    /// </remarks>
    [Test]
    public void SeedsRoundTripExactlyIncludingOnesPastLongMaxValue()
    {
        int pastSigned = 0;

        for (int opened = 0; opened < 64; opened++)
        {
            (Match match, Seat _) = _store.Open(playerCount: 2, Pace.Live, _now);

            Assert.That(_store.Find(match.Code)!.Seed, Is.EqualTo(match.Seed));

            if (match.Seed > long.MaxValue)
            {
                pastSigned++;
            }
        }

        Assert.That(
            pastSigned,
            Is.GreaterThan(0),
            "Not one seed had its top bit set, so the case this test exists for went untested.");
    }

    [Test]
    public void AnUnknownCodeIsNotFound()
    {
        Assert.That(_store.Find("ZZZZZ"), Is.Null);
    }

    // ---- Joining ------------------------------------------------------------------------

    [Test]
    public void JoinersTakeSeatsInOrder()
    {
        (Match match, Seat _) = _store.Open(playerCount: 3, Pace.Live, _now);

        (Seat? second, JoinRefusal _) = _store.Join(match.Code, _now);
        (Seat? third, JoinRefusal _) = _store.Join(match.Code, _now);

        Assert.That(second!.Number, Is.EqualTo(1));
        Assert.That(third!.Number, Is.EqualTo(2));
        Assert.That(second.Token, Is.Not.EqualTo(third.Token));
    }

    [Test]
    public void TheMatchStartsWhenTheLastSeatFills()
    {
        (Match match, Seat _) = _store.Open(playerCount: 2, Pace.Live, _now);

        Assert.That(_store.Find(match.Code)!.Started, Is.False);

        _store.Join(match.Code, _now);

        Assert.That(_store.Find(match.Code)!.Started, Is.True);
    }

    [Test]
    public void AFullLobbyRefusesAJoiner()
    {
        (Match match, Seat _) = _store.Open(playerCount: 2, Pace.Live, _now);
        _store.Join(match.Code, _now);

        (Seat? late, JoinRefusal refusal) = _store.Join(match.Code, _now);

        Assert.That(late, Is.Null);
        Assert.That(refusal, Is.EqualTo(JoinRefusal.Full));
    }

    [Test]
    public void JoiningAMatchThatIsNotThereSaysSo()
    {
        (Seat? seat, JoinRefusal refusal) = _store.Join("ZZZZZ", _now);

        Assert.That(seat, Is.Null);
        Assert.That(refusal, Is.EqualTo(JoinRefusal.NoSuchMatch));
    }

    // ---- Tokens -------------------------------------------------------------------------

    [Test]
    public void ATokenFindsItsOwnSeatAndNoOther()
    {
        (Match match, Seat host) = _store.Open(playerCount: 2, Pace.Live, _now);
        (Seat? guest, JoinRefusal _) = _store.Join(match.Code, _now);

        Assert.That(_store.SeatOf(match.Code, host.Token), Is.EqualTo(0));
        Assert.That(_store.SeatOf(match.Code, guest!.Token), Is.EqualTo(1));
    }

    /// <summary>
    /// The token is the whole of authorisation in v1, so the two ways it could leak sideways both
    /// get a test: a token nobody issued, and a real token pointed at somebody else's match.
    /// </summary>
    [Test]
    public void ATokenIsGoodForOneMatchOnly()
    {
        (Match mine, Seat host) = _store.Open(playerCount: 2, Pace.Live, _now);
        (Match theirs, Seat _) = _store.Open(playerCount: 2, Pace.Live, _now);

        Assert.That(_store.SeatOf(theirs.Code, host.Token), Is.Null);
        Assert.That(_store.SeatOf(mine.Code, "not-a-token"), Is.Null);
    }

    // ---- Rounds -------------------------------------------------------------------------

    [Test]
    public void APayloadComesBackByteForByte()
    {
        (Match match, Seat host) = _store.Open(playerCount: 2, Pace.Live, _now);
        byte[] plan = { 0x00, 0xFF, 0x10, 0x00, 0x7F, 0x80 };

        Assert.That(_store.Submit(match.Code, 1, host.Number, plan, _now), Is.True);

        IReadOnlyList<Submission> back = _store.Submissions(match.Code, 1);

        Assert.That(back, Has.Count.EqualTo(1));
        Assert.That(back[0].Payload, Is.EqualTo(plan));
        Assert.That(back[0].Seat, Is.EqualTo(0));
    }

    /// <summary>
    /// A seat that sends twice has either double-tapped commit or is trying to see what everybody
    /// else did and then change its mind. Simultaneous turns are the whole game, so the second one
    /// loses and the first one stands.
    /// </summary>
    [Test]
    public void TheFirstSubmissionForASeatWins()
    {
        (Match match, Seat host) = _store.Open(playerCount: 2, Pace.Live, _now);
        byte[] first = { 1, 1, 1 };
        byte[] second = { 2, 2, 2 };

        Assert.That(_store.Submit(match.Code, 1, host.Number, first, _now), Is.True);
        Assert.That(_store.Submit(match.Code, 1, host.Number, second, _now), Is.False);

        Assert.That(_store.Submissions(match.Code, 1)[0].Payload, Is.EqualTo(first));
    }

    [Test]
    public void TheSameSeatCanSubmitAgainInTheNextRound()
    {
        (Match match, Seat host) = _store.Open(playerCount: 2, Pace.Live, _now);

        Assert.That(_store.Submit(match.Code, 1, host.Number, new byte[] { 1 }, _now), Is.True);
        Assert.That(_store.Submit(match.Code, 2, host.Number, new byte[] { 2 }, _now), Is.True);
    }

    [Test]
    public void SubmissionsComeBackInSeatOrderWhicheverOrderTheyArrived()
    {
        (Match match, Seat host) = _store.Open(playerCount: 3, Pace.Live, _now);
        (Seat? second, JoinRefusal _) = _store.Join(match.Code, _now);
        (Seat? third, JoinRefusal _) = _store.Join(match.Code, _now);

        _store.Submit(match.Code, 1, third!.Number, new byte[] { 3 }, _now);
        _store.Submit(match.Code, 1, host.Number, new byte[] { 1 }, _now);
        _store.Submit(match.Code, 1, second!.Number, new byte[] { 2 }, _now);

        Assert.That(
            _store.Submissions(match.Code, 1).Select(submission => submission.Seat),
            Is.EqualTo(new[] { 0, 1, 2 }));
    }

    [Test]
    public void RoundsAreKeptApart()
    {
        (Match match, Seat host) = _store.Open(playerCount: 2, Pace.Live, _now);

        _store.Submit(match.Code, 1, host.Number, new byte[] { 1 }, _now);

        Assert.That(_store.Submissions(match.Code, 2), Is.Empty);
    }

    [Test]
    public void AdvancingMovesTheMatchOn()
    {
        (Match match, Seat _) = _store.Open(playerCount: 2, Pace.Live, _now);

        _store.Advance(match.Code, 2);

        Assert.That(_store.Find(match.Code)!.Round, Is.EqualTo(2));
    }

    /// <summary>
    /// Advance is driven by whoever reads the round first, so two clients polling together will both
    /// call it. It has to be safe to call twice and it must never walk a match backwards, or a late
    /// reader would drag everybody into replaying a round they have already seen.
    /// </summary>
    [Test]
    public void AdvancingNeverGoesBackwards()
    {
        (Match match, Seat _) = _store.Open(playerCount: 2, Pace.Live, _now);

        _store.Advance(match.Code, 5);
        _store.Advance(match.Code, 5);
        _store.Advance(match.Code, 2);

        Assert.That(_store.Find(match.Code)!.Round, Is.EqualTo(5));
    }

    // ---- All at once --------------------------------------------------------------------

    /// <summary>
    /// Four friends accepting an invitation in the same instant.
    /// </summary>
    /// <remarks>
    /// The store is a singleton behind a web server that answers requests in parallel, and every
    /// other test here runs on one thread, so this is the only one that sees what the deployed relay
    /// actually does. Two things could go wrong and neither would show up sequentially: the single
    /// SqliteConnection is not thread safe, and counting the seats before claiming the next one is a
    /// read-then-write that two joiners can interleave. Getting seat one handed to two players would
    /// put two platoons on the same moles.
    /// </remarks>
    [Test]
    public void FourJoinersArrivingTogetherGetFourDifferentSeats()
    {
        (Match match, Seat host) = _store.Open(playerCount: 4, Pace.Live, _now);

        Seat?[] claimed = new Seat?[3];

        Parallel.For(0, claimed.Length, at =>
        {
            (Seat? seat, JoinRefusal _) = _store.Join(match.Code, _now);
            claimed[at] = seat;
        });

        int[] numbers = claimed
            .Where(seat => seat is not null)
            .Select(seat => seat!.Number)
            .Append(host.Number)
            .OrderBy(number => number)
            .ToArray();

        Assert.That(numbers, Is.EqualTo(new[] { 0, 1, 2, 3 }), "Seats were duplicated or lost.");
        Assert.That(_store.SeatsTaken(match.Code), Is.EqualTo(4));
        Assert.That(_store.Find(match.Code)!.Started, Is.True);
    }

    /// <summary>
    /// The seat cap has to hold under load too. A lobby that oversells is worse than one that turns
    /// somebody away, because the extra player is already in the match before anybody notices.
    /// </summary>
    [Test]
    public void ALobbyNeverOversellsUnderAPileOfSimultaneousJoiners()
    {
        (Match match, Seat _) = _store.Open(playerCount: 2, Pace.Live, _now);

        int seated = 0;

        Parallel.For(0, 32, _ =>
        {
            (Seat? seat, JoinRefusal _) = _store.Join(match.Code, _now);

            if (seat is not null)
            {
                Interlocked.Increment(ref seated);
            }
        });

        Assert.That(seated, Is.EqualTo(1), "Exactly one of the pile should have got the last seat.");
        Assert.That(_store.SeatsTaken(match.Code), Is.EqualTo(2));
    }

    /// <summary>
    /// Two hosts opening a lobby at the same moment, which is the other write that races: Open draws
    /// a code, inserts it, and retries on collision.
    /// </summary>
    [Test]
    public void LobbiesOpenedTogetherGetDifferentCodesAndDifferentSeeds()
    {
        Match[] opened = new Match[16];

        Parallel.For(0, opened.Length, at =>
        {
            (Match match, Seat _) = _store.Open(playerCount: 2, Pace.Live, _now);
            opened[at] = match;
        });

        Assert.That(opened.Select(match => match.Code).Distinct().Count(), Is.EqualTo(opened.Length));
        Assert.That(opened.Select(match => match.Seed).Distinct().Count(), Is.EqualTo(opened.Length));

        foreach (Match match in opened)
        {
            Assert.That(_store.SeatsTaken(match.Code), Is.EqualTo(1), $"{match.Code} lost its host.");
        }
    }

    /// <summary>
    /// One seat double-tapping commit, from several threads. First submission wins is enforced by a
    /// primary key, so this is really asking whether the insert is serialised properly.
    /// </summary>
    [Test]
    public void OnlyOneOfAFloodOfSubmissionsForOneSeatSticks()
    {
        (Match match, Seat host) = _store.Open(playerCount: 2, Pace.Live, _now);

        int accepted = 0;

        Parallel.For(0, 32, at =>
        {
            if (_store.Submit(match.Code, 1, host.Number, new byte[] { (byte)at }, _now))
            {
                Interlocked.Increment(ref accepted);
            }
        });

        Assert.That(accepted, Is.EqualTo(1));
        Assert.That(_store.Submissions(match.Code, 1), Has.Count.EqualTo(1));
    }

    // ---- On disk ------------------------------------------------------------------------

    /// <summary>
    /// The file-backed store, which is the one that will actually be deployed.
    /// </summary>
    /// <remarks>
    /// Every other test here runs in memory, so without this the code path a real relay uses is the
    /// only one nothing covers, and the migration is written to run against a database that may
    /// already have tables in it. Reopening is the whole point: an Anytime match runs over days and
    /// a restart in the middle of one must not lose the seed, the seats or the tokens, because there
    /// is no way to reissue any of them to players who have closed the game.
    /// </remarks>
    [Test]
    public void AMatchOnDiskSurvivesTheRelayRestarting()
    {
        string file = Path.Combine(Path.GetTempPath(), $"molehill-{Guid.NewGuid():N}.sqlite");

        try
        {
            string code;
            ulong seed;
            string token;
            byte[] plan = { 0x00, 0xFF, 0x2A };

            using (MatchStore before = new MatchStore($"Data Source={file}"))
            {
                (Match match, Seat host) = before.Open(playerCount: 2, Pace.Anytime, _now);
                code = match.Code;
                seed = match.Seed;
                token = host.Token;

                before.Join(code, _now);
                before.Submit(code, 1, host.Number, plan, _now);
            }

            Assert.That(File.Exists(file), Is.True, "Nothing was written.");

            // A second store over the same file, running the same migration over existing tables.
            using MatchStore after = new MatchStore($"Data Source={file}");
            Match? found = after.Find(code);

            Assert.That(found, Is.Not.Null);
            Assert.That(found!.Seed, Is.EqualTo(seed));
            Assert.That(found.Pace, Is.EqualTo(Pace.Anytime));
            Assert.That(after.SeatsTaken(code), Is.EqualTo(2));
            Assert.That(after.SeatOf(code, token), Is.EqualTo(0));
            Assert.That(after.Submissions(code, 1)[0].Payload, Is.EqualTo(plan));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(file);
        }
    }
}
