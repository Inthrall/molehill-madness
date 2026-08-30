using Microsoft.Extensions.DependencyInjection;
using Molehill.Online;
using MoleSim.Match;
using Relay.Api;

namespace Molehill.Online.Tests;

/// <summary>
/// What one player sending rubbish costs the other three.
/// </summary>
/// <remarks>
/// The relay stores plans as opaque bytes and must keep doing so, which means the only thing
/// standing between a hostile payload and everybody's match is the client that decodes it. The
/// design is explicit about what should happen: an illegal input is dropped and "the platoon that
/// sent it does nothing that round", because every client drops the same bytes and stays in the same
/// world. What actually happened was that one unreadable payload put every client into Incompatible,
/// so any participant could end a four-player match by committing a byte of rubbish.
/// </remarks>
[TestFixture]
public sealed class HostileSeatTests
{
    /// <summary>
    /// The whole thing through a real relay: one seat sends bytes that will not decode, and the
    /// other seat plays the round anyway.
    /// </summary>
    [Test]
    public async Task APlanThatWillNotDecodeCostsOnlyTheSeatThatSentIt()
    {
        using TestRelay relay = new TestRelay($"hostile-{Guid.NewGuid():N}");
        using RelayClient one = relay.Client();
        using RelayClient two = relay.Client();

        OnlineMatch host = OnlineMatch.Hosting(one, 2, MatchPace.Live);

        await Pump(() => host.Stage == OnlineStage.WaitingForPlayers, host);

        OnlineMatch guest = OnlineMatch.Joining(two, host.Code);

        await Pump(
            () => host.Stage == OnlineStage.Planning && guest.Stage == OnlineStage.Planning,
            host, guest);

        host.Commit(Nothing(host.Seat));

        // A weapon byte naming no weapon. The relay takes it, because it is not allowed to look.
        byte[] rubbish = Nothing(guest.Seat);
        rubbish[WeaponByte] = 200;

        guest.Commit(rubbish);

        await Pump(
            () => host.Stage == OnlineStage.RoundReady && guest.Stage == OnlineStage.RoundReady,
            host, guest, seconds: 8);

        // Both still in the match, which is the whole point.
        Assert.That(host.Stage, Is.EqualTo(OnlineStage.RoundReady));
        Assert.That(guest.Stage, Is.EqualTo(OnlineStage.RoundReady));

        // One plan survived, one was dropped, and both sides agree about which.
        Assert.That(host.Plans, Has.Count.EqualTo(1));
        Assert.That(host.Plans[0].Seat, Is.EqualTo(host.Seat));
        Assert.That(host.Refused, Is.EqualTo(1));
        Assert.That(guest.Refused, Is.EqualTo(host.Refused), "Both clients must drop the same plans.");

        host.Leave();
        guest.Leave();
    }

    /// <summary>
    /// And a plan claiming a seat it was not sent from is dropped rather than believed. The relay
    /// knows who submitted, from the token; the seat written inside the plan is just a number the
    /// sender chose.
    /// </summary>
    [Test]
    public async Task APlanClaimingSomebodyElsesSeatIsDropped()
    {
        using TestRelay relay = new TestRelay($"spoof-{Guid.NewGuid():N}");
        using RelayClient one = relay.Client();
        using RelayClient two = relay.Client();

        OnlineMatch host = OnlineMatch.Hosting(one, 2, MatchPace.Live);

        await Pump(() => host.Stage == OnlineStage.WaitingForPlayers, host);

        OnlineMatch guest = OnlineMatch.Joining(two, host.Code);

        await Pump(
            () => host.Stage == OnlineStage.Planning && guest.Stage == OnlineStage.Planning,
            host, guest);

        host.Commit(Nothing(host.Seat));

        // Seat zero's number, sent from seat one's token.
        guest.Commit(Nothing(host.Seat));

        await Pump(
            () => host.Stage == OnlineStage.RoundReady && guest.Stage == OnlineStage.RoundReady,
            host, guest, seconds: 8);

        Assert.That(host.Plans, Has.Count.EqualTo(1));
        Assert.That(host.Plans[0].Seat, Is.EqualTo(host.Seat));
        Assert.That(host.Refused, Is.EqualTo(1));

        host.Leave();
        guest.Leave();
    }

    // ---- Helpers ------------------------------------------------------------------------

    /// <summary>Where the weapon sits in the wire format: version, seat, mole index, weapon.</summary>
    private const int WeaponByte = 3;

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
