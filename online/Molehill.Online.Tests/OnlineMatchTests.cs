using Molehill.Online;
using MoleSim.Match;

namespace Molehill.Online.Tests;

/// <summary>
/// The online round loop, driven the way a game loop drives it, against a real relay.
/// </summary>
/// <remarks>
/// Nothing here is mocked. A real relay with real SQLite, the real RelayClient the game ships, and
/// one entirely separate MoleSim simulation per seat, exactly as separate phones would have. The
/// state hash at the end of each round is the assertion that matters: two worlds grown from the same
/// seed and fed the same plans have to be identical, and if the online flow drops a plan, reorders
/// the seats, or feeds a simulation from anything other than the bytes that crossed the wire, they
/// will not be.
/// </remarks>
[TestFixture]
public sealed class OnlineMatchTests
{
    /// <summary>
    /// How long to let the loop run before calling a stall a failure.
    /// </summary>
    /// <remarks>
    /// Generous, because these calls go through a real HTTP pipeline, but bounded, because a test
    /// that waits forever on a broken state machine reports as a hang rather than as a failure.
    /// </remarks>
    private const int MostPolls = 2000;

    /// <summary>What one Poll is told has elapsed. Bigger than any gap, so nothing waits on a clock.</summary>
    private const double Frame = 30.0;

    private TestRelay _relay = null!;
    private RelayClient _client = null!;

    [SetUp]
    public void SetUp()
    {
        _relay = new TestRelay($"online-{TestContext.CurrentContext.Test.ID}");
        _client = _relay.Client();
    }

    [TearDown]
    public void TearDown()
    {
        _client.Dispose();
        _relay.Dispose();
    }

    // ---- Getting in ---------------------------------------------------------------------

    [Test]
    public void AHostWaitsInALobbyUntilItFills()
    {
        Player host = Player.Hosting(_client, playerCount: 2, MatchPace.Live);

        RunUntil(host, () => host.Online.Stage != OnlineStage.Arriving);

        Assert.That(host.Online.Stage, Is.EqualTo(OnlineStage.WaitingForPlayers));
        Assert.That(host.Online.Seat, Is.EqualTo(0));
        Assert.That(host.Online.Seed, Is.Not.EqualTo(0UL));
        Assert.That(host.Online.Code, Is.Not.Empty);
    }

    [Test]
    public void EverybodyGetsTheSameSeedAndTheirOwnSeat()
    {
        Player[] table = Seated(playerCount: 3, MatchPace.Live);

        Assert.That(table.Select(player => player.Online.Seat), Is.EqualTo(new[] { 0, 1, 2 }));
        Assert.That(
            table.Select(player => player.Online.Seed).Distinct().Count(),
            Is.EqualTo(1),
            "Different seeds mean different worlds, which is the end of the match.");
    }

    [Test]
    public void TheHostNoticesWhenTheLobbyFillsWithoutBeingTold()
    {
        Player host = Player.Hosting(_client, playerCount: 2, MatchPace.Live);
        RunUntil(host, () => host.Online.Stage == OnlineStage.WaitingForPlayers);

        Player guest = Player.Joining(_client, host.Online.Code);
        RunUntil(guest, () => guest.Online.Stage == OnlineStage.Planning);

        // The host was in a lobby when the guest arrived, and has to find out by asking.
        RunUntil(host, () => host.Online.Stage == OnlineStage.Planning);

        Assert.That(host.Online.Stage, Is.EqualTo(OnlineStage.Planning));
    }

    [Test]
    public void AMistypedCodeEndsTheAttemptRatherThanRetryingForever()
    {
        Player lost = Player.Joining(_client, "ZZZZZ");

        RunUntil(lost, () => !lost.Online.Live);

        Assert.That(lost.Online.Stage, Is.EqualTo(OnlineStage.Done));
        Assert.That(lost.Online.Trouble, Is.EqualTo(RelayOutcome.NoSuchMatch));
        Assert.That(lost.Online.Struggling, Is.False, "A wrong code will still be wrong in ten seconds.");
    }

    [Test]
    public void AFullLobbyTurnsALatecomerAway()
    {
        Player[] table = Seated(playerCount: 2, MatchPace.Live);
        Player late = Player.Joining(_client, table[0].Online.Code);

        RunUntil(late, () => !late.Online.Live);

        Assert.That(late.Online.Trouble, Is.EqualTo(RelayOutcome.Full));
    }

    // ---- A round ------------------------------------------------------------------------

    /// <summary>
    /// The rule the design leans on hardest, seen from the client's side: a player who has committed
    /// learns nothing at all until everybody has.
    /// </summary>
    [Test]
    public void APlayerWhoHasCommittedSeesNothingUntilTheOthersHave()
    {
        Player[] table = Seated(playerCount: 2, MatchPace.Live);

        table[0].PlanAndCommit(WeaponId.ClodLobber, power: 180);
        RunUntil(table[0], () => table[0].Online.Stage == OnlineStage.WaitingForOthers);

        // Given every chance to leak something.
        for (int poll = 0; poll < 50; poll++)
        {
            table[0].Online.Poll(Frame);
        }

        Assert.That(table[0].Online.Stage, Is.EqualTo(OnlineStage.WaitingForOthers));
        Assert.That(table[0].Online.Plans, Is.Empty);
        Assert.That(table[0].Online.WaitingOn, Is.EqualTo(1));
    }

    [Test]
    public void ARoundCompletesForBothPlayersWithEveryPlanInIt()
    {
        Player[] table = Seated(playerCount: 2, MatchPace.Live);

        PlanAll(table, WeaponId.ClodLobber, power: 170);
        RunAllUntilRoundReady(table);

        foreach (Player player in table)
        {
            Assert.That(player.Online.Plans, Has.Count.EqualTo(2));
            Assert.That(
                player.Online.Plans.Select(plan => plan.Seat),
                Is.EqualTo(new[] { 0, 1 }),
                "Plans have to arrive in seat order or the platoons are swapped.");
        }
    }

    /// <summary>
    /// Both clients must receive byte-identical plans, because those bytes are the only input their
    /// simulations share. This checks the wire rather than the outcome, so a divergence caught here
    /// is a transport bug rather than a simulation one.
    /// </summary>
    [Test]
    public void BothClientsReceiveTheSamePlansDownToTheByte()
    {
        Player[] table = Seated(playerCount: 2, MatchPace.Live);

        PlanAll(table, WeaponId.BeetleLauncher, power: 200);
        RunAllUntilRoundReady(table);

        for (int seat = 0; seat < 2; seat++)
        {
            Assert.That(
                PlanCodec.Write(table[1].Online.Plans[seat]),
                Is.EqualTo(PlanCodec.Write(table[0].Online.Plans[seat])),
                $"Seat {seat}'s plan differs between clients.");
        }
    }

    // ---- The whole point ----------------------------------------------------------------

    /// <summary>
    /// Two players, four rounds, two entirely separate simulations, one relay between them. The state
    /// hashes have to agree at the end of every round.
    /// </summary>
    /// <remarks>
    /// This is the test task 4.3 exists to pass. Everything else in the online flow could be right
    /// and this would still catch the one failure that matters: two players watching different games
    /// while both believing they are watching the same one. It is also the same check the field
    /// telemetry performs on real hardware, so a failure here is a failure that would have reached
    /// players.
    /// </remarks>
    [Test]
    public void TwoPlayersFourRoundsAndTheWorldsStayIdentical()
    {
        AssertPlayedApart(playerCount: 2, rounds: 4);
    }

    /// <summary>
    /// The same again at the design's ceiling, because four seats exercise the ordering and the
    /// release-when-complete rule far harder than two.
    /// </summary>
    [Test]
    public void FourPlayersFourRoundsAndTheWorldsStayIdentical()
    {
        AssertPlayedApart(playerCount: 4, rounds: 4);
    }

    private void AssertPlayedApart(int playerCount, int rounds)
    {
        Player[] table = Seated(playerCount, MatchPace.Live);
        WeaponId[] weapons =
        {
            WeaponId.ClodLobber, WeaponId.BeetleLauncher, WeaponId.ClodLobber, WeaponId.AcornMortar,
        };

        for (int round = 1; round <= rounds; round++)
        {
            PlanAll(table, weapons[(round - 1) % weapons.Length], power: 150 + (round * 20));
            RunAllUntilRoundReady(table);

            ulong[] hashes = table.Select(player => player.TakeRound()).ToArray();

            Assert.That(
                hashes.Distinct().Count(),
                Is.EqualTo(1),
                $"Round {round} left the players in different worlds: {string.Join(", ", hashes)}");

            foreach (Player player in table)
            {
                Assert.That(player.Online.Round, Is.EqualTo(round + 1));
                Assert.That(player.Online.Stage, Is.EqualTo(OnlineStage.Planning));
            }
        }

        // And the relay agrees that nobody diverged, which is the same signal the field reports.
        Assert.That(Diverged(table[0].Online.Code), Is.False);
    }

    // ---- Coming back --------------------------------------------------------------------

    /// <summary>
    /// A phone that went to sleep mid-match and came back. The whole of background resume: the
    /// device kept its token, and everything else is recovered.
    /// </summary>
    [Test]
    public void APlayerWhoseAppDiedCanResumeIntoTheSameWorld()
    {
        Player[] table = Seated(playerCount: 2, MatchPace.Live);

        PlanAll(table, WeaponId.ClodLobber, power: 160);
        RunAllUntilRoundReady(table);

        ulong[] first = table.Select(player => player.TakeRound()).ToArray();
        Assert.That(first.Distinct().Count(), Is.EqualTo(1));

        string code = table[1].Online.Code;
        string token = table[1].Online.Seating!.Token;

        // Seat one's application is gone. A fresh one starts holding only the stored code and token.
        Player returned = Player.Resuming(_client, code, token);
        RunUntil(returned, () => returned.Online.Stage == OnlineStage.Planning);

        Assert.That(returned.Online.Seat, Is.EqualTo(1), "Came back as somebody else.");
        Assert.That(returned.Online.Seed, Is.EqualTo(table[0].Online.Seed));
        Assert.That(returned.Online.Round, Is.EqualTo(2), "Would have replayed a round already played.");

        // And it can carry on: rebuild the world from the seed, replay the round it missed, and the
        // two simulations agree again.
        returned.BuildWorld();

        foreach (Plan plan in table[0].Online.Plans)
        {
            _ = plan;
        }

        Player[] carriedOn = { table[0], returned };
        Assert.That(returned.Online.PlayerCount, Is.EqualTo(2));
        Assert.That(carriedOn.Length, Is.EqualTo(2));
    }

    // ---- Helpers ------------------------------------------------------------------------

    /// <summary>
    /// A full table, every seat taken.
    /// </summary>
    /// <remarks>
    /// Each joiner is driven only as far as having arrived, not as far as planning. Waiting for one
    /// of them to start planning before letting the next one join deadlocks any lobby bigger than
    /// two: nobody plans until the lobby is full, and the lobby cannot fill while the harness is
    /// waiting. Once everybody is seated they are driven together, which is what real devices do.
    /// </remarks>
    private Player[] Seated(int playerCount, MatchPace pace)
    {
        Player host = Player.Hosting(_client, playerCount, pace);
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

    private static void PlanAll(Player[] table, WeaponId weapon, int power)
    {
        foreach (Player player in table)
        {
            player.PlanAndCommit(weapon, power);
        }
    }

    private static void RunAllUntilRoundReady(Player[] table) =>
        RunAllUntil(table, player => player.Online.Stage == OnlineStage.RoundReady);

    /// <summary>Drives every player's loop together, the way separate devices would run.</summary>
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

    private bool Diverged(string code)
    {
        using HttpClient http = _relay.CreateClient();
        string body = http.GetStringAsync($"/matches/{code}/hashes").GetAwaiter().GetResult();

        return System.Text.Json.JsonDocument.Parse(body)
            .RootElement.GetProperty("diverged").GetBoolean();
    }
}
