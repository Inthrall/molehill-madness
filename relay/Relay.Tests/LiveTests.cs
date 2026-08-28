using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Relay.Api;

namespace Relay.Tests;

/// <summary>
/// The live hub: what it says, when it says it, and what happens when somebody stops listening.
/// </summary>
/// <remarks>
/// Split the way the forfeit sweep is split, and for the same reason. Every decision lives in
/// <see cref="LiveWatch"/> and is tested against a store and a handed-in clock, with no socket
/// anywhere near it; the socket tests are separate and are about the socket, which is the part that
/// cannot be reasoned about and has to be run.
/// </remarks>
[TestFixture]
public sealed class LiveTests
{
    private MatchStore _store = null!;
    private DateTimeOffset _now;

    [SetUp]
    public void SetUp()
    {
        _store = MatchStore.InMemory($"live-{TestContext.CurrentContext.Test.ID}");
        _now = new DateTimeOffset(2026, 8, 28, 9, 0, 0, TimeSpan.Zero);
    }

    [TearDown]
    public void TearDown() => _store.Dispose();

    // ---- What is worth saying -------------------------------------------------------------

    /// <summary>
    /// A first look says nothing about how full the lobby is. Somebody who has just connected has
    /// the seat count in the reply that gave them their seat, and announcing it again as though it
    /// had changed would have a lobby flicker every time anybody opened a socket.
    /// </summary>
    [Test]
    public void TheFirstLookAtAQuietMatchSaysNothing()
    {
        Match match = Started(2);

        (IReadOnlyList<byte[]> notices, LiveWatch.Seen _) =
            LiveWatch.Look(_store, match.Code, LiveWatch.Seen.Nothing);

        Assert.That(notices, Is.Empty);
    }

    [Test]
    public void SomebodyTakingASeatIsWorthSaying()
    {
        (Match opened, Seat _) = _store.Open(3, Pace.Live, _now);

        (IReadOnlyList<byte[]> first, LiveWatch.Seen after) =
            LiveWatch.Look(_store, opened.Code, LiveWatch.Seen.Nothing);

        Assert.That(first, Is.Empty);

        _store.Join(opened.Code, _now);

        (IReadOnlyList<byte[]> then, LiveWatch.Seen _) =
            LiveWatch.Look(_store, opened.Code, after);

        Assert.That(then, Has.Count.EqualTo(1));
        Assert.That(LiveNotice.Read(then[0]), Does.Contain("\"kind\":\"seated\""));
        Assert.That(LiveNotice.Read(then[0]), Does.Contain("\"seated\":2"));
    }

    /// <summary>
    /// The bell rings once. A settled round stays settled until somebody reads it and moves the
    /// match on, so the naive check is true on every tick in between and would ring four times a
    /// second until it did.
    /// </summary>
    [Test]
    public void ASettledRoundIsAnnouncedOnceRatherThanEveryTick()
    {
        Match match = Started(2);

        LiveWatch.Seen seen = Look(match.Code, LiveWatch.Seen.Nothing, out _);

        _store.Submit(match.Code, 1, 0, new byte[] { 1 }, _now);

        seen = Look(match.Code, seen, out IReadOnlyList<byte[]> half);
        Assert.That(half, Is.Empty, "Half a round is not a round.");

        _store.Submit(match.Code, 1, 1, new byte[] { 2 }, _now);

        seen = Look(match.Code, seen, out IReadOnlyList<byte[]> rung);

        Assert.That(rung, Has.Count.EqualTo(1));
        Assert.That(LiveNotice.Read(rung[0]), Does.Contain("\"kind\":\"round\""));
        Assert.That(LiveNotice.Read(rung[0]), Does.Contain("\"round\":1"));

        Look(match.Code, seen, out IReadOnlyList<byte[]> again);

        Assert.That(again, Is.Empty, "The same round was announced twice.");
    }

    [Test]
    public void TheNextRoundIsAnnouncedInItsTurn()
    {
        Match match = Started(2);

        LiveWatch.Seen seen = Look(match.Code, LiveWatch.Seen.Nothing, out _);

        _store.Submit(match.Code, 1, 0, new byte[] { 1 }, _now);
        _store.Submit(match.Code, 1, 1, new byte[] { 2 }, _now);
        seen = Look(match.Code, seen, out _);

        _store.Advance(match.Code, 2, _now);
        seen = Look(match.Code, seen, out IReadOnlyList<byte[]> moved);
        Assert.That(moved, Is.Empty, "Moving on is not news; the round being ready is.");

        _store.Submit(match.Code, 2, 0, new byte[] { 3 }, _now);
        _store.Submit(match.Code, 2, 1, new byte[] { 4 }, _now);

        Look(match.Code, seen, out IReadOnlyList<byte[]> rung);

        Assert.That(rung, Has.Count.EqualTo(1));
        Assert.That(LiveNotice.Read(rung[0]), Does.Contain("\"round\":2"));
    }

    /// <summary>
    /// A client that connects to a match whose round has already settled is told at once rather than
    /// left to poll for something that has already happened. It is the case a reconnection is.
    /// </summary>
    [Test]
    public void ARoundThatSettledBeforeAnybodyWasListeningIsStillAnnounced()
    {
        Match match = Started(2);

        _store.Submit(match.Code, 1, 0, new byte[] { 1 }, _now);
        _store.Submit(match.Code, 1, 1, new byte[] { 2 }, _now);

        Look(match.Code, LiveWatch.Seen.Nothing, out IReadOnlyList<byte[]> notices);

        Assert.That(notices, Has.Count.EqualTo(1));
        Assert.That(LiveNotice.Read(notices[0]), Does.Contain("\"kind\":\"round\""));
    }

    [Test]
    public void ChangingPaceIsWorthSaying()
    {
        Match match = Started(2);

        LiveWatch.Seen seen = Look(match.Code, LiveWatch.Seen.Nothing, out _);

        _store.Downgrade(match.Code, Pace.Anytime, RoundWindow.Default);

        Look(match.Code, seen, out IReadOnlyList<byte[]> notices);

        Assert.That(notices, Has.Count.EqualTo(1));
        Assert.That(LiveNotice.Read(notices[0]), Does.Contain("\"kind\":\"pace\""));
        Assert.That(LiveNotice.Read(notices[0]), Does.Contain("\"pace\":\"Anytime\""));
        Assert.That(LiveNotice.Read(notices[0]), Does.Contain("\"windowSeconds\":86400"));
    }

    // ---- Losing somebody --------------------------------------------------------------------

    /// <summary>
    /// A Live match has no deadline, deliberately, because everybody is present. This is what
    /// happens when that stops being true: without it, one player walking away leaves three others
    /// waiting on a round that can never settle.
    /// </summary>
    [Test]
    public void ALiveMatchWaitingOnSomebodyWhoIsNotThereIsAbandoned()
    {
        Match match = Started(2, Pace.Live);

        _store.Submit(match.Code, 1, 0, new byte[] { 1 }, _now);

        Assert.That(
            LiveWatch.Abandoned(_store, match.Code, new[] { 0 }, _now + LiveWatch.Patience),
            Is.True);
    }

    [Test]
    public void SomebodyMerelySlowIsNotAbandoned()
    {
        Match match = Started(2, Pace.Live);

        _store.Submit(match.Code, 1, 0, new byte[] { 1 }, _now);

        // Both on a socket, so seat one is thinking rather than gone, however long it takes.
        Assert.That(
            LiveWatch.Abandoned(_store, match.Code, new[] { 0, 1 }, _now + TimeSpan.FromHours(1)),
            Is.False);
    }

    [Test]
    public void NobodyIsAbandonedBeforeThePatienceRunsOut()
    {
        Match match = Started(2, Pace.Live);

        _store.Submit(match.Code, 1, 0, new byte[] { 1 }, _now);

        Assert.That(
            LiveWatch.Abandoned(
                _store, match.Code, new[] { 0 }, _now + LiveWatch.Patience - TimeSpan.FromSeconds(1)),
            Is.False);
    }

    /// <summary>
    /// A seat that has answered can leave. Their plan is in, the round is not waiting on them, and
    /// somebody closing the game after committing is an ordinary thing to do.
    /// </summary>
    [Test]
    public void ASeatThatHasAlreadyCommittedMayGoAway()
    {
        Match match = Started(2, Pace.Live);

        _store.Submit(match.Code, 1, 0, new byte[] { 1 }, _now);
        _store.Submit(match.Code, 1, 1, new byte[] { 2 }, _now);

        Assert.That(
            LiveWatch.Abandoned(
                _store, match.Code, Array.Empty<int>(), _now + TimeSpan.FromHours(1)),
            Is.False);
    }

    [Test]
    public void AnAnytimeMatchIsNeverAbandonedThisWay()
    {
        Match match = Started(2, Pace.Anytime);

        Assert.That(
            LiveWatch.Abandoned(
                _store, match.Code, Array.Empty<int>(), _now + TimeSpan.FromHours(1)),
            Is.False,
            "Anytime has a window and a forfeit sweep; it does not need rescuing.");
    }

    /// <summary>
    /// The downgrade is what turns an abandoned Live match into one the forfeit sweep can finish,
    /// and it hangs the window off when the round opened rather than off when somebody noticed.
    /// </summary>
    [Test]
    public void DowngradingGivesTheRoundAWindowAndThenLeavesItAlone()
    {
        Match match = Started(2, Pace.Live);

        Assert.That(match.Deadline, Is.Null, "Live pace has no deadline.");

        _store.Downgrade(match.Code, Pace.Anytime, RoundWindow.Default);

        Match after = _store.Find(match.Code)!;

        Assert.That(after.Pace, Is.EqualTo(Pace.Anytime));
        Assert.That(after.Deadline, Is.EqualTo(match.RoundOpenedAt.AddSeconds(RoundWindow.Default)));

        // And it cannot happen twice, so a watcher that keeps looking cannot keep pushing the
        // deadline out and give everybody a free day every quarter of a second.
        _store.Downgrade(match.Code, Pace.Anytime, RoundWindow.Shortest);

        Assert.That(
            _store.Find(match.Code)!.WindowSeconds, Is.EqualTo(RoundWindow.Default));
    }

    // ---- The socket itself --------------------------------------------------------------------

    [Test]
    public async Task ASocketWithTheRightTokenHearsWhatTheHubSays()
    {
        using RelayFactory relay = new RelayFactory($"live-socket-{Guid.NewGuid():N}");
        using HttpClient client = relay.CreateClient();

        Joined host = await Open(client, 2);

        using WebSocket socket = await Listen(relay, host.Code, host.Token);

        LiveHub hub = relay.Services.GetRequiredService<LiveHub>();

        // Waited for rather than assumed: the endpoint registers the listener after the handshake
        // finishes, and the client is told the handshake finished a moment before that happens.
        await Until(() => hub.Present(host.Code).Count == 1);

        await hub.Tell(host.Code, LiveNotice.Round(3));

        Assert.That(await Heard(socket), Does.Contain("\"round\":3"));
    }

    [Test]
    public async Task ASocketWithoutASeatTokenIsRefused()
    {
        using RelayFactory relay = new RelayFactory($"live-refused-{Guid.NewGuid():N}");
        using HttpClient client = relay.CreateClient();

        Joined host = await Open(client, 2);

        Assert.That(
            async () => await Listen(relay, host.Code, "not-a-token"),
            Throws.InstanceOf<InvalidOperationException>(),
            "The socket must not upgrade for somebody who is not in the match.");
    }

    /// <summary>
    /// Everybody in the match hears it at the same moment, which is the entire reason the hub is
    /// worth having: four phones polling on their own second see a round up to a second apart.
    /// </summary>
    [Test]
    public async Task EverybodyListeningHearsIt()
    {
        using RelayFactory relay = new RelayFactory($"live-both-{Guid.NewGuid():N}");
        using HttpClient client = relay.CreateClient();

        Joined host = await Open(client, 2);
        Joined guest = await Join(client, host.Code);

        using WebSocket first = await Listen(relay, host.Code, host.Token);
        using WebSocket second = await Listen(relay, host.Code, guest.Token);

        LiveHub hub = relay.Services.GetRequiredService<LiveHub>();

        await Until(() => hub.Present(host.Code).Count == 2);

        await hub.Tell(host.Code, LiveNotice.Round(1));

        Assert.That(await Heard(first), Does.Contain("\"round\":1"));
        Assert.That(await Heard(second), Does.Contain("\"round\":1"));
    }

    [Test]
    public async Task HangingUpTakesTheListenerOutOfTheList()
    {
        using RelayFactory relay = new RelayFactory($"live-gone-{Guid.NewGuid():N}");
        using HttpClient client = relay.CreateClient();

        Joined host = await Open(client, 2);
        LiveHub hub = relay.Services.GetRequiredService<LiveHub>();

        using (WebSocket socket = await Listen(relay, host.Code, host.Token))
        {
            await Until(() => hub.Present(host.Code).Count == 1);

            await socket.CloseAsync(
                WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        }

        await Until(() => hub.Watched().Count == 0);

        Assert.That(hub.Present(host.Code), Is.Empty);
    }

    /// <summary>
    /// The whole thing, through the real relay: two seats commit, and the socket says so without
    /// anybody asking. This is the one test that covers the watcher, the hub and the endpoint at
    /// once, and the only one that would notice if the watcher stopped being registered at all.
    /// </summary>
    [Test]
    public async Task CommittingTheLastPlanRingsTheBell()
    {
        using RelayFactory relay = new RelayFactory($"live-bell-{Guid.NewGuid():N}");
        using HttpClient client = relay.CreateClient();

        Joined host = await Open(client, 2);
        Joined guest = await Join(client, host.Code);

        using WebSocket socket = await Listen(relay, host.Code, host.Token);

        LiveHub hub = relay.Services.GetRequiredService<LiveHub>();

        await Until(() => hub.Present(host.Code).Count == 1);

        await client.PostPlan(host.Code, 1, host.Token, new byte[] { 1, 2, 3 });
        await client.PostPlan(host.Code, 1, guest.Token, new byte[] { 4, 5, 6 });

        Assert.That(await Heard(socket), Does.Contain("\"kind\":\"round\""));
    }

    // ---- Helpers ------------------------------------------------------------------------

    private LiveWatch.Seen Look(
        string code, LiveWatch.Seen before, out IReadOnlyList<byte[]> notices)
    {
        (IReadOnlyList<byte[]> said, LiveWatch.Seen now) = LiveWatch.Look(_store, code, before);

        notices = said;

        return now;
    }

    private Match Started(int players, Pace pace = Pace.Live)
    {
        (Match opened, Seat _) = _store.Open(players, pace, _now);

        for (int seat = 1; seat < players; seat++)
        {
            _store.Join(opened.Code, _now);
        }

        return _store.Find(opened.Code)!;
    }

    private static async Task<Joined> Open(HttpClient client, int players)
    {
        HttpResponseMessage opened = await client.PostAsJsonAsync(
            "/lobbies", new OpenLobby(players, Pace.Live));

        return await opened.AsJoined();
    }

    private static async Task<Joined> Join(HttpClient client, string code)
    {
        HttpResponseMessage joined = await client.PostAsync($"/lobbies/{code}/seats", null);

        return await joined.AsJoined();
    }

    private static async Task<WebSocket> Listen(RelayFactory relay, string code, string token)
    {
        WebSocketClient sockets = relay.Server.CreateWebSocketClient();

        sockets.ConfigureRequest = request => request.Headers.Add("X-Seat-Token", token);

        return await sockets.ConnectAsync(
            new Uri(relay.Server.BaseAddress, $"matches/{code}/live"), CancellationToken.None);
    }

    /// <summary>One notice off a socket, or a failed test rather than a hung one.</summary>
    private static async Task<string> Heard(WebSocket socket)
    {
        byte[] buffer = new byte[1024];

        using CancellationTokenSource giveUp = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        WebSocketReceiveResult heard =
            await socket.ReceiveAsync(new ArraySegment<byte>(buffer), giveUp.Token);

        return Encoding.UTF8.GetString(buffer, 0, heard.Count);
    }

    /// <summary>
    /// Waits for something the server does on its own time.
    /// </summary>
    /// <remarks>
    /// A socket handshake finishing and the listener being registered are two different moments, and
    /// the client is told about the first. Polling for the second is honest about that; sleeping for
    /// a guessed interval instead is the same test with a flake in it.
    /// </remarks>
    private static async Task Until(Func<bool> ready)
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            if (ready())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail("The relay never got there.");
    }
}
