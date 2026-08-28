using Molehill.Online;

namespace Molehill.Online.Tests;

/// <summary>
/// Pressing the one button: an account, a place in the pool, and a match with strangers.
/// </summary>
/// <remarks>
/// Driven through the real relay with its real sweep running, because the interesting part of
/// matchmaking is two clients that have never heard of each other arriving in the same lobby, and a
/// test that seated them by hand would be a test of nothing.
///
/// The one about a child is the one that matters. The design's safety argument rests on a single
/// sentence, and this is the client end of the only place it is enforced.
/// </remarks>
[TestFixture]
public sealed class PoolTests
{
    private static readonly DateTimeOffset Whenever =
        new DateTimeOffset(2026, 8, 29, 9, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Two people who have never met press the same button and end up in the same match, and what
    /// they get is an ordinary match: a code, a seat each, a seed they both generate the world from.
    /// </summary>
    [Test]
    public async Task TwoStrangersPressTheButtonAndEndUpPlayingEachOther()
    {
        using TestRelay relay = new TestRelay($"pool-{Guid.NewGuid():N}");
        using RelayClient one = relay.Client();
        using RelayClient two = relay.Client();

        AccountKey first = await Account(one, AgeBand.Adult);
        AccountKey second = await Account(two, AgeBand.Adult);

        OnlineMatch host = OnlineMatch.Matchmaking(one, first, AgeBand.Adult, 2, MatchPace.Live);
        OnlineMatch stranger = OnlineMatch.Matchmaking(two, second, AgeBand.Adult, 2, MatchPace.Live);

        Assert.That(host.Stage, Is.EqualTo(OnlineStage.Queueing));

        await Pump(
            () => host.Stage == OnlineStage.Planning && stranger.Stage == OnlineStage.Planning,
            host, stranger);

        Assert.That(host.Code, Is.EqualTo(stranger.Code));
        Assert.That(host.Seed, Is.EqualTo(stranger.Seed));
        Assert.That(host.Seat, Is.Not.EqualTo(stranger.Seat));
        Assert.That(host.PlayerCount, Is.EqualTo(2));

        // And it plays like any other match, because it is one. Nothing downstream of the pool knows
        // or could know how these two found each other.
        host.Commit(Nothing(host.Seat));
        stranger.Commit(Nothing(stranger.Seat));

        await Pump(
            () => host.Stage == OnlineStage.RoundReady && stranger.Stage == OnlineStage.RoundReady,
            host, stranger);

        Assert.That(host.Plans, Has.Count.EqualTo(2));

        host.Leave();
        stranger.Leave();
    }

    /// <summary>
    /// The rule the whole age gate exists for, seen from the client. The relay refuses, and the
    /// refusal arrives as its own outcome rather than a generic no, because it is the only one a
    /// player is owed an explanation for: a button that failed silently here would look broken to
    /// exactly the person it is protecting.
    /// </summary>
    [TestCase(AgeBand.Child)]
    [TestCase(AgeBand.Unknown)]
    public async Task AnAccountUnderTheThresholdIsNotPutAmongStrangers(AgeBand band)
    {
        using TestRelay relay = new TestRelay($"pool-young-{Guid.NewGuid():N}");
        using RelayClient client = relay.Client();

        AccountKey account = await Account(client, band);

        OnlineMatch match = OnlineMatch.Matchmaking(client, account, band, 2, MatchPace.Live);

        await Pump(() => match.Stage == OnlineStage.Done, match);

        Assert.That(match.Trouble, Is.EqualTo(RelayOutcome.TooYoung));
    }

    /// <summary>
    /// And the same account can still play with people it knows, because a code is not a stranger.
    /// Gating this would stop a child playing with their own family while doing nothing about the
    /// risk the gate exists for.
    /// </summary>
    [Test]
    public async Task AChildMayStillJoinByCode()
    {
        using TestRelay relay = new TestRelay($"pool-code-{Guid.NewGuid():N}");
        using RelayClient one = relay.Client();
        using RelayClient two = relay.Client();

        await Account(two, AgeBand.Child);

        OnlineMatch host = OnlineMatch.Hosting(one, 2, MatchPace.Live);

        await Pump(() => host.Stage == OnlineStage.WaitingForPlayers, host);

        OnlineMatch child = OnlineMatch.Joining(two, host.Code);

        await Pump(() => child.Stage == OnlineStage.Planning, host, child);

        Assert.That(child.Code, Is.EqualTo(host.Code));

        host.Leave();
        child.Leave();
    }

    /// <summary>
    /// A player who has had the birthday that moves their band tells the relay, and is let in.
    /// </summary>
    [Test]
    public async Task GrowingUpChangesTheAnswer()
    {
        using TestRelay relay = new TestRelay($"pool-grown-{Guid.NewGuid():N}");
        using RelayClient client = relay.Client();

        AccountKey account = await Account(client, AgeBand.Child);

        Reply<bool> moved = await client.SetBand(account, AgeBand.Adult);

        Assert.That(moved.Ok, Is.True);

        OnlineMatch match = OnlineMatch.Matchmaking(client, account, AgeBand.Adult, 2, MatchPace.Live);

        // Nobody else is queueing, so waiting is the right answer and Done would be the wrong one.
        await Pump(() => match.Queued, match, seconds: 3);

        Assert.That(match.Stage, Is.EqualTo(OnlineStage.Queueing));
        Assert.That(match.Trouble, Is.Not.EqualTo(RelayOutcome.TooYoung));

        match.Leave();
    }

    /// <summary>
    /// The design's answer to a thin pool is the other pace, not a better spinner, and the relay says
    /// when to offer it. What to do about it belongs to the screen: switching somebody's pace without
    /// asking would be answering a different question from the one they pressed the button for.
    /// </summary>
    [Test]
    public async Task AQueueThatIsNotFillingOffersTheOtherPace()
    {
        Clock clock = new Clock(Whenever);

        using TestRelay relay = new TestRelay($"pool-slow-{Guid.NewGuid():N}", clock);
        using RelayClient client = relay.Client();

        AccountKey account = await Account(client, AgeBand.Adult);

        OnlineMatch match = OnlineMatch.Matchmaking(client, account, AgeBand.Adult, 4, MatchPace.Live);

        await Pump(() => match.Queued, match, seconds: 3);

        // Several rounds of asking with the clock where it is. Nobody is slow after a moment, and
        // the point of asking more than once is that a bug here would show up as slow immediately
        // rather than never.
        for (int tick = 0; tick < 300; tick++)
        {
            match.Poll(0.01);

            await Task.Delay(5);
        }

        Assert.That(match.PoolIsSlow, Is.False, "Nobody is slow after a moment.");

        clock.Pass(TimeSpan.FromSeconds(60));

        await Pump(() => match.PoolIsSlow, match);

        Assert.That(match.Stage, Is.EqualTo(OnlineStage.Queueing), "Still queueing, just told.");

        match.Leave();
    }

    /// <summary>
    /// Somebody who walks away comes out of the pool. Left in, they would be seated into a match
    /// nobody is coming to, and a stranger would be held there waiting for them.
    /// </summary>
    [Test]
    public async Task LeavingTheQueueGivesThePlaceBack()
    {
        using TestRelay relay = new TestRelay($"pool-left-{Guid.NewGuid():N}");
        using RelayClient client = relay.Client();
        using RelayClient other = relay.Client();

        AccountKey account = await Account(client, AgeBand.Adult);

        OnlineMatch match = OnlineMatch.Matchmaking(client, account, AgeBand.Adult, 2, MatchPace.Live);

        await Pump(() => match.Queued, match, seconds: 3);

        match.Leave();

        // Whoever turns up next must not be matched with a player who has gone.
        AccountKey second = await Account(other, AgeBand.Adult);
        OnlineMatch after = OnlineMatch.Matchmaking(other, second, AgeBand.Adult, 2, MatchPace.Live);

        await Pump(() => after.Queued, after, seconds: 3);

        // Three seconds is three passes of the relay's own sweep, so if the place that was given up
        // were still in the pool this one would be sitting in a match by now.
        await Task.Delay(3000);

        Assert.That(after.Stage, Is.EqualTo(OnlineStage.Queueing));

        after.Leave();
    }

    /// <summary>
    /// A device that has never needed an account gets one on the way into the pool, and hands it
    /// straight to whoever is going to write it down.
    /// </summary>
    /// <remarks>
    /// Not made at startup and not made at install: couch play needs no account and a game code
    /// needs none either, so the first time one exists is the first time somebody asks to be put
    /// with strangers. The relay issues the secret once and cannot reissue it, which is why the
    /// callback matters more than it looks: a client that did not keep it has thrown the account
    /// away without noticing.
    /// </remarks>
    [Test]
    public async Task ADeviceWithNoAccountGetsOneOnTheWayIn()
    {
        using TestRelay relay = new TestRelay($"pool-new-{Guid.NewGuid():N}");
        using RelayClient client = relay.Client();

        AccountKey? kept = null;

        OnlineMatch match = OnlineMatch.Matchmaking(
            client, null, AgeBand.Adult, 2, MatchPace.Live, account => kept = account);

        await Pump(() => match.Queued, match, seconds: 4);

        Assert.That(kept, Is.Not.Null, "The one and only copy of the secret was dropped.");
        Assert.That(kept!.Id, Is.Not.Empty);
        Assert.That(kept.Secret, Is.Not.Empty);

        match.Leave();
    }

    /// <summary>
    /// A device that has never been through the gate is sent to it rather than given an account and
    /// refused one call later. The relay would refuse it anyway; this is about not making a player
    /// wait for a round trip to be told what the client already knew.
    /// </summary>
    [Test]
    public void ADeviceThatHasNotBeenAskedDoesNotEvenGetAnAccount()
    {
        using TestRelay relay = new TestRelay($"pool-ungated-{Guid.NewGuid():N}");
        using RelayClient client = relay.Client();

        OnlineMatch match = OnlineMatch.Matchmaking(
            client, null, AgeBand.Unknown, 2, MatchPace.Live);

        Assert.That(match.Stage, Is.EqualTo(OnlineStage.Done));
        Assert.That(match.Trouble, Is.EqualTo(RelayOutcome.TooYoung));
    }

    // ---- Helpers ------------------------------------------------------------------------

    private static async Task<AccountKey> Account(RelayClient client, AgeBand band)
    {
        Reply<AccountKey> made = await client.OpenAccount(band);

        Assert.That(made.Ok, Is.True, $"The relay would not make an account: {made.Outcome}.");

        return made.Value!;
    }

    private static byte[] Nothing(int seat) =>
        MoleSim.Match.PlanCodec.Write(new MoleSim.Match.Plan(
            seat,
            0,
            MoleSim.Match.WeaponId.None,
            Array.Empty<MoleSim.Match.RoutePoint>(),
            Array.Empty<MoleSim.Match.PlanAction>()));

    /// <summary>Polls every match until they get where they are going, or the test fails.</summary>
    private static async Task Pump(
        Func<bool> there, OnlineMatch first, OnlineMatch? second = null, double seconds = 8)
    {
        for (int tick = 0; tick < seconds * 100; tick++)
        {
            if (there())
            {
                return;
            }

            first.Poll(0.01);
            second?.Poll(0.01);

            await Task.Delay(10);
        }

        Assert.Fail(
            $"Nobody got there: {first.Stage} ({first.Trouble})"
            + (second is null ? string.Empty : $", {second.Stage} ({second.Trouble})"));
    }

    private static Task Pump(Func<bool> there, OnlineMatch first, OnlineMatch second) =>
        Pump(there, first, second, 8);
}
