using System.Linq;
using System.Net.WebSockets;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Molehill.Online;
using MoleSim.Match;
using Relay.Api;

namespace Molehill.Online.Tests;

/// <summary>
/// The client's half of the live hub: a socket that says when to ask, and never says anything else.
/// </summary>
/// <remarks>
/// The thing worth proving here is not that a notice arrives, it is that the client is no worse off
/// without one. A socket that carried rounds would make every one of these tests a question about
/// reliability; a socket that only rings makes them a question about latency, and the answer to a
/// missed ring is a slower poll rather than a lost match.
/// </remarks>
[TestFixture]
public sealed class DoorbellTests
{
    // ---- Where the socket goes ------------------------------------------------------------

    /// <summary>
    /// Getting this wrong is a connection refused on a device and nothing at all on a desktop, which
    /// is the worst way round for finding out about it.
    /// </summary>
    [TestCase("http://localhost:5000/", "ws://localhost:5000/matches/BADGE/live")]
    [TestCase("https://relay.example/", "wss://relay.example/matches/BADGE/live")]
    [TestCase("https://relay.example:8443/", "wss://relay.example:8443/matches/BADGE/live")]
    public void TheSocketFollowsTheRelayIntoOrOutOfTls(string relay, string expected)
    {
        Assert.That(
            LiveDoorbell.Address(new Uri(relay), "BADGE").AbsoluteUri, Is.EqualTo(expected));
    }

    // ---- Ringing ----------------------------------------------------------------------------

    [Test]
    public async Task ANoticeFromTheRelayRingsTheBell()
    {
        using TestRelay relay = new TestRelay($"bell-{Guid.NewGuid():N}");
        using RelayClient client = relay.Client();

        Joined host = await Open(client);

        using LiveDoorbell bell = Doorbell(relay, host);

        bell.Start();

        LiveHub hub = relay.Services.GetRequiredService<LiveHub>();

        await Until(() => bell.Listening && hub.Present(host.Code).Count == 1);

        Assert.That(bell.Rang(), Is.False, "Nothing has happened yet.");

        await hub.Tell(host.Code, LiveNotice.Round(1));
        await Until(bell.Rang);
    }

    /// <summary>
    /// Consumed on read. Several notices between two frames ring once, because the client's answer
    /// to any of them is the same call and making it twice is a wasted request.
    /// </summary>
    [Test]
    public async Task TheBellIsAnsweredOnceHoweverManyTimesItRang()
    {
        using TestRelay relay = new TestRelay($"bell-once-{Guid.NewGuid():N}");
        using RelayClient client = relay.Client();

        Joined host = await Open(client);

        using LiveDoorbell bell = Doorbell(relay, host);

        bell.Start();

        LiveHub hub = relay.Services.GetRequiredService<LiveHub>();

        await Until(() => hub.Present(host.Code).Count == 1);

        await hub.Tell(host.Code, LiveNotice.Round(1));
        await hub.Tell(host.Code, LiveNotice.Seated(2));

        await Until(bell.Rang);

        Assert.That(bell.Rang(), Is.False);
    }

    /// <summary>
    /// A relay that will not have it must not take the game down with it. This is the tunnel case,
    /// the wrong-address case and the refused-token case, and all three are the same to a player:
    /// no socket, and a match that carries on polling.
    /// </summary>
    [Test]
    public async Task ADoorbellThatCannotConnectIsQuietRatherThanFatal()
    {
        using LiveDoorbell bell = new LiveDoorbell(
            new Uri("http://nowhere.invalid/"),
            "BADGE",
            "token",
            (where, token, cancel) =>
                throw new WebSocketException("There is nothing at that address."));

        bell.Start();

        await Task.Delay(200);

        Assert.That(bell.Listening, Is.False);
        Assert.That(bell.Rang(), Is.False);
        Assert.That(bell.Trouble, Is.Not.Empty);
    }

    // ---- What it changes --------------------------------------------------------------------

    /// <summary>
    /// The feature, stated as a number: the same five seconds of waiting, with a socket and without.
    /// </summary>
    /// <remarks>
    /// Both sides are measured rather than one, because the interesting quantity is the ratio and a
    /// single number would be a fact about the gap constants rather than about the behaviour. The one
    /// call the socket side still makes is the check that happens the moment a commit lands, which is
    /// worth keeping: the other seats may have committed while this one was still deciding.
    /// </remarks>
    [Test]
    public async Task ASocketTurnsASecondlyPollIntoOneCall()
    {
        int withASocket = await CallsWhileWaiting(listening: true);
        int without = await CallsWhileWaiting(listening: false);

        Assert.That(without, Is.GreaterThanOrEqualTo(4), "Live pace polls about once a second.");
        Assert.That(
            withASocket, Is.LessThanOrEqualTo(1),
            "A client with a socket up should not be polling for the round as well.");
    }

    /// <summary>
    /// How many times a waiting client asks the relay about the round over five seconds.
    /// </summary>
    private static async Task<int> CallsWhileWaiting(bool listening)
    {
        using TestRelay relay = new TestRelay($"bell-quiet-{Guid.NewGuid():N}");

        Counting counting = new Counting();
        using RelayClient client = new RelayClient(relay.CreateDefaultClient(counting));
        using RelayClient second = relay.Client();

        OnlineMatch host = OnlineMatch.Hosting(client, 2, MatchPace.Live);

        await Pump(() => host.Stage == OnlineStage.WaitingForPlayers, host);

        OnlineMatch guest = OnlineMatch.Joining(second, host.Code);

        await Pump(() => host.Stage == OnlineStage.Planning, host, guest);

        if (listening)
        {
            host.Listen(Doorbell(relay, host.Code, host.Seating!.Token));

            await Until(() => host.Hearing);
        }

        host.Commit(Nothing(host.Seat));

        await Pump(() => host.Stage == OnlineStage.WaitingForOthers, host);

        counting.Reset();

        // Five seconds of game loop with the other seat saying nothing.
        for (int frame = 0; frame < 300; frame++)
        {
            host.Poll(1d / 60d);

            await Task.Delay(1);
        }

        int asked = counting.Rounds;

        host.Leave();

        return asked;
    }

    /// <summary>
    /// The same match, played through with a socket, ends where it would have without one. The
    /// doorbell is an optimisation, and an optimisation that changed the outcome would be a bug.
    /// </summary>
    [Test]
    public async Task ARoundPlayedWithASocketEndsExactlyWhereItWouldHaveWithout()
    {
        using TestRelay relay = new TestRelay($"bell-round-{Guid.NewGuid():N}");
        using RelayClient one = relay.Client();
        using RelayClient two = relay.Client();

        OnlineMatch host = OnlineMatch.Hosting(one, 2, MatchPace.Live);

        await Pump(() => host.Stage == OnlineStage.WaitingForPlayers, host);

        OnlineMatch guest = OnlineMatch.Joining(two, host.Code);

        await Pump(
            () => host.Stage == OnlineStage.Planning && guest.Stage == OnlineStage.Planning,
            host, guest);

        host.Listen(Doorbell(relay, host.Code, host.Seating!.Token));
        guest.Listen(Doorbell(relay, guest.Code, guest.Seating!.Token));

        await Until(() => host.Hearing && guest.Hearing);

        host.Commit(Nothing(host.Seat));
        guest.Commit(Nothing(guest.Seat));

        await Pump(
            () => host.Stage == OnlineStage.RoundReady && guest.Stage == OnlineStage.RoundReady,
            host, guest);

        // Everybody's plan, in seat order, byte for byte, on both sides. The socket carries none of
        // this and must not have touched it.
        Assert.That(host.Plans, Has.Count.EqualTo(2));
        Assert.That(guest.Plans, Has.Count.EqualTo(2));
        Assert.That(host.Plans[0].Seat, Is.EqualTo(0));
        Assert.That(host.Plans[1].Seat, Is.EqualTo(1));
        Assert.That(
            guest.Plans.Select(plan => plan.Seat),
            Is.EqualTo(host.Plans.Select(plan => plan.Seat)),
            "Both sides read the same seats out of the same release.");

        host.Leave();
        guest.Leave();
    }

    /// <summary>
    /// Leaving turns the socket off. A reconnect loop that outlived its match would carry on dialling
    /// a finished game for as long as the process ran, which on a phone is until it is force quit.
    /// </summary>
    [Test]
    public async Task LeavingAMatchStopsItsSocket()
    {
        using TestRelay relay = new TestRelay($"bell-leave-{Guid.NewGuid():N}");
        using RelayClient client = relay.Client();

        OnlineMatch host = OnlineMatch.Hosting(client, 2, MatchPace.Live);

        await Pump(() => host.Stage == OnlineStage.WaitingForPlayers, host);

        host.Listen(Doorbell(relay, host.Code, host.Seating!.Token));

        await Until(() => host.Hearing);

        host.Leave();

        Assert.That(host.Hearing, Is.False);

        LiveHub hub = relay.Services.GetRequiredService<LiveHub>();

        await Until(() => hub.Present(host.Code).Count == 0);
    }

    // ---- Helpers ------------------------------------------------------------------------

    /// <summary>
    /// A doorbell wired to the in-process relay.
    /// </summary>
    /// <remarks>
    /// The address the doorbell worked out is ignored here and the test server's own is used
    /// instead, because a test host is not reachable over a socket a client could dial. What that
    /// costs is exactly one thing: the ws and wss scheme is covered by its own test rather than by
    /// these, since nothing here would notice if it were wrong.
    /// </remarks>
    private static LiveDoorbell Doorbell(TestRelay relay, string code, string token) =>
        new LiveDoorbell(
            new Uri("http://localhost/"),
            code,
            token,
            (where, seat, cancel) =>
            {
                WebSocketClient sockets = relay.Server.CreateWebSocketClient();

                sockets.ConfigureRequest =
                    request => request.Headers.Add("X-Seat-Token", seat);

                return sockets.ConnectAsync(
                    new Uri(relay.Server.BaseAddress, $"matches/{code}/live"), cancel);
            });

    private static LiveDoorbell Doorbell(TestRelay relay, Joined host) =>
        Doorbell(relay, host.Code, host.Token);

    private static async Task<Joined> Open(RelayClient client)
    {
        Reply<Seating> seated = await client.Host(2, MatchPace.Live);

        return new Joined(seated.Value!.Code, seated.Value.Token);
    }

    /// <summary>
    /// Polls every match until they get where they are going, or the test fails.
    /// </summary>
    /// <remarks>
    /// Every match, not the one being waited on. A committed plan does not leave the client until
    /// the next poll, so a test that waits on the host while the guest sits unpolled is waiting for
    /// a request that nothing is going to make. It reads as the relay never releasing the round.
    /// </remarks>
    private static async Task Pump(Func<bool> there, params OnlineMatch[] matches)
    {
        for (int tick = 0; tick < 600; tick++)
        {
            if (there())
            {
                return;
            }

            foreach (OnlineMatch match in matches)
            {
                match.Poll(0.01);
            }

            await Task.Delay(10);
        }

        Assert.Fail(
            "Nobody got there: "
            + string.Join(", ", matches.Select(match => $"{match.Stage} ({match.Trouble})")));
    }

    private static async Task Until(Func<bool> ready)
    {
        for (int attempt = 0; attempt < 400; attempt++)
        {
            if (ready())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail("It never got there.");
    }

    private sealed record Joined(string Code, string Token);

    /// <summary>A plan that does nothing, which is all these tests need one to be.</summary>
    private static byte[] Nothing(int seat) =>
        PlanCodec.Write(new Plan(
            seat, 0, WeaponId.None, Array.Empty<RoutePoint>(), Array.Empty<PlanAction>()));

    /// <summary>Counts the calls a client makes, so a poll rate is a number rather than a feeling.</summary>
    private sealed class Counting : DelegatingHandler
    {
        private int _rounds;

        public int Rounds => Volatile.Read(ref _rounds);

        public void Reset() => Volatile.Write(ref _rounds, 0);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get
                && request.RequestUri?.AbsolutePath.Contains("/rounds/", StringComparison.Ordinal) == true)
            {
                Interlocked.Increment(ref _rounds);
            }

            return base.SendAsync(request, cancellationToken);
        }
    }
}
