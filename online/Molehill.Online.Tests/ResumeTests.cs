using Molehill.Online;
using MoleSim.Match;

namespace Molehill.Online.Tests;

/// <summary>
/// Coming back to a match that has been going on without you.
/// </summary>
/// <remarks>
/// The whole premise of Anytime pace is that a match outlives the app being open, so resuming is not
/// an edge case, it is the ordinary way those matches are played. A resuming client used to be given
/// the round the match had reached and a world built from the seed alone, which is a pristine map
/// with full-pluck moles: everything it drew was fiction, every hash it reported was a mismatch, and
/// nothing said so.
///
/// The measure of the fix is the only one that means anything here. Two clients that have taken
/// different routes to the same round have to hold byte-identical worlds, because that is the
/// property the entire online design rests on.
/// </remarks>
[TestFixture]
public sealed class ResumeTests
{
    private const int MapWidthCells = 400;
    private const int MapHeightCells = 240;

    /// <summary>
    /// One client plays three rounds. Another arrives at round four and replays its way there. The
    /// two simulations have to agree exactly.
    /// </summary>
    [Test]
    public async Task AResumingClientRebuildsTheSameWorldTheOthersAreIn()
    {
        using TestRelay relay = new TestRelay($"resume-{Guid.NewGuid():N}");
        using RelayClient one = relay.Client();
        using RelayClient two = relay.Client();

        OnlineMatch host = OnlineMatch.Hosting(one, 2, MatchPace.Live);

        await Pump(() => host.Stage == OnlineStage.WaitingForPlayers, host);

        OnlineMatch guest = OnlineMatch.Joining(two, host.Code);

        await Pump(
            () => host.Stage == OnlineStage.Planning && guest.Stage == OnlineStage.Planning,
            host, guest);

        MoleMatch played = MoleMatch.Create(
            host.PlayerCount, host.Seed, MapWidthCells, MapHeightCells);

        string code = host.Code;
        string token = host.Seating!.Token;

        for (int round = 0; round < 3; round++)
        {
            host.Commit(Nothing(host.Seat));
            guest.Commit(Nothing(guest.Seat));

            await Pump(
                () => host.Stage == OnlineStage.RoundReady && guest.Stage == OnlineStage.RoundReady,
                host, guest, seconds: 8);

            RoundFeeder.Feed(played, host.Plans);
            played.ResolveRound();

            host.RoundTaken();
            guest.RoundTaken();

            await Pump(() => host.Stage == OnlineStage.Planning, host, guest);
        }

        Assert.That(host.Round, Is.EqualTo(4), "Three rounds played, so the fourth is next.");

        // Somebody closes the game and comes back. All this device has is the code and its token.
        using RelayClient again = relay.Client();

        OnlineMatch resumed = OnlineMatch.Resuming(again, code, token);
        MoleMatch rebuilt = null!;

        await Pump(
            () =>
            {
                if (resumed.Seating is not null && rebuilt is null)
                {
                    rebuilt = MoleMatch.Create(
                        resumed.PlayerCount, resumed.Seed, MapWidthCells, MapHeightCells);
                }

                if (resumed.Stage == OnlineStage.RoundReady && rebuilt is not null)
                {
                    RoundFeeder.Feed(rebuilt, resumed.Plans);
                    rebuilt.ResolveRound();
                    resumed.RoundTaken();
                }

                return !resumed.CatchingUp && resumed.Stage == OnlineStage.Planning;
            },
            resumed,
            seconds: 12);

        Assert.That(resumed.Round, Is.EqualTo(4), "Caught up to the round being played.");
        Assert.That(rebuilt, Is.Not.Null);
        Assert.That(
            rebuilt.StateHash(), Is.EqualTo(played.StateHash()),
            "A resumed world that differs from the one everybody else is in is a silent desync.");

        host.Leave();
        guest.Leave();
        resumed.Leave();
    }

    /// <summary>
    /// And arriving at a match still on its first round has nothing to catch up on, so it goes
    /// straight to planning as it always did.
    /// </summary>
    [Test]
    public async Task ArrivingAtTheFirstRoundIsNotCatchingUp()
    {
        using TestRelay relay = new TestRelay($"resume-first-{Guid.NewGuid():N}");
        using RelayClient one = relay.Client();
        using RelayClient two = relay.Client();

        OnlineMatch host = OnlineMatch.Hosting(one, 2, MatchPace.Live);

        await Pump(() => host.Stage == OnlineStage.WaitingForPlayers, host);

        OnlineMatch guest = OnlineMatch.Joining(two, host.Code);

        await Pump(() => guest.Stage == OnlineStage.Planning, host, guest);

        Assert.That(guest.CatchingUp, Is.False);
        Assert.That(guest.Round, Is.EqualTo(1));

        host.Leave();
        guest.Leave();
    }

    // ---- Helpers ------------------------------------------------------------------------

    private static byte[] Nothing(int seat) =>
        PlanCodec.Write(new Plan(
            seat, 0, WeaponId.None, Array.Empty<RoutePoint>(), Array.Empty<PlanAction>()));

    private static async Task Pump(
        Func<bool> there, OnlineMatch first, OnlineMatch? second = null, double seconds = 6)
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
}
