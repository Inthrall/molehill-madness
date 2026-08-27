using Microsoft.Extensions.DependencyInjection;
using Molehill.Online;
using MoleSim.Match;

namespace Molehill.Online.Tests;

/// <summary>
/// Anytime pace: a match played over days, and what happens when somebody stops turning up.
/// </summary>
/// <remarks>
/// The failure this exists to prevent is a match of four people ended by one of them losing interest.
/// Without a window, the other three wait on a plan that is never coming and there is nothing they
/// can do about it, because the only participant who could notice is the one who is waiting.
///
/// The clock is driven by hand, which is the whole reason the relay takes its time from a
/// TimeProvider. Otherwise the shortest testable window would be the shortest window the relay
/// allows, a minute, and nobody would run these.
/// </remarks>
[TestFixture]
public sealed class AnytimeTests
{
    private const int MostPolls = 2000;
    private const double Frame = 30.0;

    private Clock _clock = null!;
    private TestRelay _relay = null!;
    private RelayClient _client = null!;

    [SetUp]
    public void SetUp()
    {
        _clock = new Clock(new DateTimeOffset(2026, 8, 28, 9, 0, 0, TimeSpan.Zero));
        _relay = new TestRelay($"anytime-{TestContext.CurrentContext.Test.ID}", _clock);
        _client = _relay.Client();
    }

    [TearDown]
    public void TearDown()
    {
        _client.Dispose();
        _relay.Dispose();
    }

    [Test]
    public void AnAnytimeMatchCarriesADeadlineAndALivePaceOneDoesNot()
    {
        Player anytime = Player.Hosting(_client, 2, MatchPace.Anytime, windowSeconds: 3600);
        RunUntil(anytime, () => anytime.Online.Seating is not null);

        Player live = Player.Hosting(_client, 2, MatchPace.Live);
        RunUntil(live, () => live.Online.Seating is not null);

        Assert.That(anytime.Online.Seating!.Deadline, Is.Not.Null);
        Assert.That(anytime.Online.Seating.WindowSeconds, Is.EqualTo(3600));
        Assert.That(
            live.Online.Seating!.Deadline,
            Is.Null,
            "Everybody is present in Live pace, so nobody can run out of time.");
    }

    /// <summary>
    /// The point of the whole task. One player never commits, the window closes, and the other three
    /// get on with the game.
    /// </summary>
    [Test]
    public void ARoundResolvesWithoutThePlayerWhoNeverCameBack()
    {
        Player[] table = Seated(3, MatchPace.Anytime, windowSeconds: 3600);

        // Two of three commit. The third player has closed the game and gone out for the day.
        table[0].PlanAndCommit(WeaponId.ClodLobber, power: 170);
        table[1].PlanAndCommit(WeaponId.BeetleLauncher, power: 190);

        RunAllUntil(table[..2], player => player.Online.Stage == OnlineStage.WaitingForOthers);

        // Nothing happens while the window is open, however long anybody polls.
        for (int poll = 0; poll < 60; poll++)
        {
            table[0].Online.Poll(Frame);
        }

        Assert.That(table[0].Online.Stage, Is.EqualTo(OnlineStage.WaitingForOthers));

        _clock.Pass(TimeSpan.FromHours(2));

        RunAllUntil(table[..2], player => player.Online.Stage == OnlineStage.RoundReady);

        foreach (Player player in table[..2])
        {
            Assert.That(player.Online.Plans, Has.Count.EqualTo(2), "Two plans, not three.");
            Assert.That(
                player.Online.Forfeited,
                Is.EqualTo(new[] { 2 }),
                "Seat two ran out of window and should be named as having done nothing.");
        }

        // And both remaining players still end the round in the same world, which is the part that
        // would break if a forfeit were an invented empty plan on one client and nothing on another.
        ulong[] hashes = table[..2].Select(player => player.TakeRound()).ToArray();

        Assert.That(hashes.Distinct().Count(), Is.EqualTo(1));
    }

    /// <summary>
    /// A forfeit is a seat doing nothing, and it must be exactly that. Feeding the simulation an
    /// invented empty plan instead would put the relay in the business of knowing what a plan is, and
    /// would give a different answer from feeding it nothing at all.
    /// </summary>
    [Test]
    public void AForfeitedSeatSimplyDoesNothing()
    {
        Player[] table = Seated(2, MatchPace.Anytime, windowSeconds: 3600);

        table[0].PlanAndCommit(WeaponId.ClodLobber, power: 170);
        RunAllUntil(table[..1], player => player.Online.Stage == OnlineStage.WaitingForOthers);

        _clock.Pass(TimeSpan.FromHours(2));
        RunAllUntil(table[..1], player => player.Online.Stage == OnlineStage.RoundReady);

        // Kept before the round is taken, because taking it clears the released plans.
        ulong seed = table[0].Online.Seed;
        Plan committed = table[0].Online.Plans[0];

        ulong afterForfeit = table[0].TakeRound();

        // The same round, played out with only seat zero's plan and no notion of seat one at all.
        MoleMatch alone = MoleMatch.Create(2, seed, 400, 240);
        RoundFeeder.Feed(alone, new[] { committed });
        alone.ResolveRound();

        Assert.That(
            alone.StateHash(),
            Is.EqualTo(afterForfeit),
            "A forfeit has to be indistinguishable from that platoon having no plan.");
    }

    /// <summary>
    /// A player who wakes up and commits before the window closes keeps their turn, and nobody
    /// forfeits. The window is a backstop, not a race.
    /// </summary>
    [Test]
    public void APlayerWhoCommitsInTimeIsNotForfeited()
    {
        Player[] table = Seated(2, MatchPace.Anytime, windowSeconds: 3600);

        table[0].PlanAndCommit(WeaponId.ClodLobber, power: 170);
        RunAllUntil(table[..1], player => player.Online.Stage == OnlineStage.WaitingForOthers);

        _clock.Pass(TimeSpan.FromMinutes(50));

        table[1].PlanAndCommit(WeaponId.AcornMortar, power: 150);
        RunAllUntil(table, player => player.Online.Stage == OnlineStage.RoundReady);

        foreach (Player player in table)
        {
            Assert.That(player.Online.Plans, Has.Count.EqualTo(2));
            Assert.That(player.Online.Forfeited, Is.Empty);
        }
    }

    /// <summary>
    /// Each round gets its own window. A match where one round ran long must not start the next one
    /// already overdue.
    /// </summary>
    [Test]
    public void TheNextRoundGetsAFreshWindow()
    {
        Player[] table = Seated(2, MatchPace.Anytime, windowSeconds: 3600);

        table[0].PlanAndCommit(WeaponId.ClodLobber, power: 170);
        RunAllUntil(table[..1], player => player.Online.Stage == OnlineStage.WaitingForOthers);

        _clock.Pass(TimeSpan.FromHours(2));
        RunAllUntil(table[..1], player => player.Online.Stage == OnlineStage.RoundReady);
        table[0].TakeRound();

        // Round two, and seat zero commits straight away. Nothing should be overdue yet.
        table[0].PlanAndCommit(WeaponId.ClodLobber, power: 170);
        RunAllUntil(table[..1], player => player.Online.Stage == OnlineStage.WaitingForOthers);

        for (int poll = 0; poll < 60; poll++)
        {
            table[0].Online.Poll(Frame);
        }

        Assert.That(
            table[0].Online.Stage,
            Is.EqualTo(OnlineStage.WaitingForOthers),
            "Round two forfeited on round one's clock.");
    }

    // ---- Being told it is your turn -----------------------------------------------------

    /// <summary>
    /// A device registers, a round resolves, and a notification is queued for the player who has not
    /// committed yet.
    /// </summary>
    /// <remarks>
    /// This is the whole of push notification that can be verified here. Whether the message reaches
    /// a phone is Firebase's business and there is no Firebase project to point it at, but whether the
    /// right person is chosen at the right moment is ours, and it is the half with a rule in it.
    /// </remarks>
    [Test]
    public async Task ARoundResolvingQueuesANotificationForWhoeverIsNext()
    {
        Player[] table = Seated(2, MatchPace.Anytime, windowSeconds: 3600);

        Assert.That(
            (await _client.RegisterDevice(
                table[1].Online.Code,
                table[1].Online.Seating!.Token,
                "a-push-token",
                "android")).Ok,
            Is.True);

        // Round one goes through with both plans, so the relay moves to round two and decides.
        table[0].PlanAndCommit(WeaponId.ClodLobber, power: 170);
        table[1].PlanAndCommit(WeaponId.AcornMortar, power: 150);
        RunAllUntil(table, player => player.Online.Stage == OnlineStage.RoundReady);

        Assert.That(
            Pending(),
            Has.Count.EqualTo(1),
            "Seat one has a device and has not committed for round two, so it should be told.");
    }

    [Test]
    public async Task ADeviceTokenWithoutASeatTokenIsRefused()
    {
        Player[] table = Seated(2, MatchPace.Anytime, windowSeconds: 3600);

        Reply<bool> reply = await _client.RegisterDevice(
            table[0].Online.Code, "not-a-token", "a-push-token", "android");

        Assert.That(reply.Outcome, Is.EqualTo(RelayOutcome.NotYourSeat));
    }

    /// <summary>
    /// A push token comes from a platform, so it is not this code's to assume anything about. One
    /// with a quote in it must not be able to break the request it travels in.
    /// </summary>
    [Test]
    public async Task ADeviceTokenWithAwkwardCharactersInItSurvives()
    {
        Player[] table = Seated(2, MatchPace.Anytime, windowSeconds: 3600);

        Reply<bool> reply = await _client.RegisterDevice(
            table[0].Online.Code,
            table[0].Online.Seating!.Token,
            "tok\"en\\with/awkward\tcharacters",
            "android");

        Assert.That(reply.Ok, Is.True);
    }

    private IReadOnlyList<Relay.Api.Nudge> Pending()
    {
        using IServiceScope scope = _relay.Services.CreateScope();

        return scope.ServiceProvider.GetRequiredService<Relay.Api.MatchStore>().PendingNudges();
    }

    // ---- Helpers ------------------------------------------------------------------------

    private Player[] Seated(int playerCount, MatchPace pace, int windowSeconds = 0)
    {
        Player host = Player.Hosting(_client, playerCount, pace, windowSeconds);
        RunUntil(host, () => host.Online.Stage != OnlineStage.Arriving);

        List<Player> table = new List<Player> { host };

        for (int seat = 1; seat < playerCount; seat++)
        {
            Player joiner = Player.Joining(_client, host.Online.Code);
            RunUntil(joiner, () => joiner.Online.Stage != OnlineStage.Arriving);
            table.Add(joiner);
        }

        Player[] seated = table.ToArray();
        RunAllUntil(seated, player => player.Online.Stage == OnlineStage.Planning);

        foreach (Player player in seated)
        {
            player.BuildWorld();
        }

        return seated;
    }

    private static void RunAllUntil(Player[] table, Func<Player, bool> done)
    {
        for (int poll = 0; poll < MostPolls; poll++)
        {
            bool everybody = true;

            foreach (Player player in table)
            {
                player.Online.Poll(Frame);

                if (!done(player))
                {
                    everybody = false;
                }

                Assert.That(
                    player.Online.Live,
                    Is.True,
                    $"Seat {player.Online.Seat} dropped out: {player.Online.Trouble}");
            }

            if (everybody)
            {
                return;
            }

            Thread.Sleep(1);
        }

        Assert.Fail(
            "Never got there. Stages: "
            + string.Join(", ", table.Select(player => player.Online.Stage)));
    }

    private static void RunUntil(Player player, Func<bool> done)
    {
        for (int poll = 0; poll < MostPolls; poll++)
        {
            if (done())
            {
                return;
            }

            player.Online.Poll(Frame);
            Thread.Sleep(1);
        }

        Assert.Fail($"Stuck at {player.Online.Stage} ({player.Online.Trouble}).");
    }
}
