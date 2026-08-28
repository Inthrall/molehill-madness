using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Relay.Api;

namespace Relay.Tests;

/// <summary>
/// Accounts, the age gate, and the pool that pairs strangers up.
/// </summary>
/// <remarks>
/// One of these tests matters more than the rest and it is the one about a child. The design's whole
/// safety argument rests on a single sentence, that "random matchmaking with strangers is gated to
/// accounts over the threshold", and this is the only place in the system where that sentence is
/// enforced rather than described. The client has the same rule written down and the client's copy
/// runs on a machine the player owns.
/// </remarks>
[TestFixture]
public sealed class MatchmakingTests
{
    private MatchStore _store = null!;
    private DateTimeOffset _now;

    [SetUp]
    public void SetUp()
    {
        _store = MatchStore.InMemory($"pool-{TestContext.CurrentContext.Test.ID}");
        _now = new DateTimeOffset(2026, 8, 29, 9, 0, 0, TimeSpan.Zero);
    }

    [TearDown]
    public void TearDown() => _store.Dispose();

    // ---- Accounts ---------------------------------------------------------------------

    [Test]
    public void AnAccountIsAnIdASecretAndABand()
    {
        (Account made, string secret) = _store.OpenAccount(AgeBand.Adult, _now);

        Assert.That(made.Id, Is.Not.Empty);
        Assert.That(secret, Is.Not.Empty);
        Assert.That(made.Band, Is.EqualTo(AgeBand.Adult));

        Account? found = _store.Who(made.Id, secret, _now);

        Assert.That(found, Is.Not.Null);
        Assert.That(found!.Band, Is.EqualTo(AgeBand.Adult));
    }

    [Test]
    public void AnAccountIsOnlyItsOwnSecretsToOpen()
    {
        (Account mine, string _) = _store.OpenAccount(AgeBand.Adult, _now);
        (Account _, string yours) = _store.OpenAccount(AgeBand.Adult, _now);

        Assert.That(_store.Who(mine.Id, yours, _now), Is.Null);
        Assert.That(_store.Who(mine.Id, "guessed", _now), Is.Null);
        Assert.That(_store.Who("not-an-account", yours, _now), Is.Null);
    }

    /// <summary>
    /// A child becomes an adult while the account carries on existing, so the band has to move. The
    /// client re-asks on the birthday it worked the band out from and sends the new answer.
    /// </summary>
    [Test]
    public void ABandCanChangeWhenSomebodyHasABirthday()
    {
        (Account account, string secret) = _store.OpenAccount(AgeBand.Child, _now);

        Assert.That(_store.SetBand(account.Id, secret, AgeBand.Adult, _now), Is.True);
        Assert.That(_store.Who(account.Id, secret, _now)!.Band, Is.EqualTo(AgeBand.Adult));

        Assert.That(
            _store.SetBand(account.Id, "not the secret", AgeBand.Child, _now), Is.False,
            "Anybody could move anybody else's band.");
    }

    // ---- The gate ---------------------------------------------------------------------

    /// <summary>
    /// The one rule the age gate exists for. Adults among strangers, nobody else, and in particular
    /// not an account that has never been asked: silence is not consent, and an account that skipped
    /// the gate must not look like one that passed it.
    /// </summary>
    [TestCase(AgeBand.Adult, true)]
    [TestCase(AgeBand.Child, false)]
    [TestCase(AgeBand.Unknown, false)]
    public void OnlyAnAdultAccountIsLetInAmongStrangers(AgeBand band, bool allowed)
    {
        Assert.That(Allowed.Matchmaking(band), Is.EqualTo(allowed));
    }

    /// <summary>
    /// And nothing about a band touches a game code. A code arrives from somebody you know, and
    /// gating it would stop a child playing with their own family while doing nothing at all about
    /// the risk the gate exists for.
    /// </summary>
    [TestCase(AgeBand.Adult)]
    [TestCase(AgeBand.Child)]
    [TestCase(AgeBand.Unknown)]
    public void EverybodyMayJoinByCode(AgeBand band)
    {
        Assert.That(Allowed.JoiningByCode(band), Is.True);
    }

    // ---- Pairing ----------------------------------------------------------------------

    /// <summary>
    /// Oldest first is the only fairness rule in the pool. Without it, a queue receiving players
    /// faster than it can seat them would leave whoever arrived first waiting longest.
    /// </summary>
    [Test]
    public void ThePoolSeatsThePeopleWhoHaveWaitedLongest()
    {
        Ticket first = Waiting("first", 2, minutes: 5);
        Ticket second = Waiting("second", 2, minutes: 3);
        Ticket third = Waiting("third", 2, minutes: 1);

        IReadOnlyList<IReadOnlyList<Ticket>> made =
            Matchmaker.Pair(new[] { third, first, second }, 2);

        Assert.That(made, Has.Count.EqualTo(1));
        Assert.That(made[0].Select(ticket => ticket.Account), Is.EqualTo(new[] { "first", "second" }));
    }

    /// <summary>
    /// Three people who asked for a four are still waiting for a four. A host in a lobby may lower
    /// the count and start, because a host is a person making a decision; nothing in the pool is in
    /// a position to make that one on somebody's behalf.
    /// </summary>
    [Test]
    public void APartialGroupIsLeftWaitingRatherThanSeatedShort()
    {
        Ticket[] three = { Waiting("one", 4), Waiting("two", 4), Waiting("three", 4) };

        Assert.That(Matchmaker.Pair(three, 4), Is.Empty);
    }

    [Test]
    public void AskingForDifferentSizesIsAskingForDifferentMatches()
    {
        Ticket[] mixed =
        {
            Waiting("duel one", 2), Waiting("duel two", 2),
            Waiting("four one", 4), Waiting("four two", 4),
        };

        Assert.That(Matchmaker.Pair(mixed, 2), Has.Count.EqualTo(1));
        Assert.That(Matchmaker.Pair(mixed, 4), Is.Empty);
    }

    [Test]
    public void SomebodyAlreadySeatedIsNotPairedAgain()
    {
        Ticket seated = Waiting("seated", 2) with { Code = "BADGE" };
        Ticket alone = Waiting("alone", 2);

        Assert.That(Matchmaker.Pair(new[] { seated, alone }, 2), Is.Empty);
    }

    // ---- Being told the pool is thin ----------------------------------------------------

    /// <summary>
    /// The design's answer to an empty pool is the other pace rather than a better spinner, and it
    /// is offered early on purpose: a player who has watched a queue for a minute has already
    /// decided the game is dead.
    /// </summary>
    [Test]
    public void ALiveQueueThatIsNotFillingSaysSo()
    {
        Ticket ticket = Waiting("waiting", 2);

        Assert.That(Matchmaker.Slowly(ticket, _now), Is.False);
        Assert.That(
            Matchmaker.Slowly(ticket, _now + Matchmaker.Slow - TimeSpan.FromSeconds(1)), Is.False);
        Assert.That(Matchmaker.Slowly(ticket, _now + Matchmaker.Slow), Is.True);
    }

    /// <summary>
    /// Anytime is what somebody slow is offered, so offering it to somebody already playing it would
    /// be offering them what they have.
    /// </summary>
    [Test]
    public void NobodyIsOfferedThePaceTheyAlreadyChose()
    {
        Ticket ticket = Waiting("patient", 2, pace: Pace.Anytime);

        Assert.That(Matchmaker.Slowly(ticket, _now + TimeSpan.FromHours(1)), Is.False);
    }

    [Test]
    public void SomebodyAlreadyInAMatchIsNotWaitingSlowly()
    {
        Ticket seated = Waiting("seated", 2) with { Code = "BADGE" };

        Assert.That(Matchmaker.Slowly(seated, _now + TimeSpan.FromHours(1)), Is.False);
    }

    // ---- Filling ------------------------------------------------------------------------

    /// <summary>
    /// A matchmade match is an ordinary match. It has a code somebody could read out, its seats came
    /// from the same Join everybody else uses, and nothing downstream can tell how its players found
    /// each other, which is why every rule about rounds and forfeits already works on it.
    /// </summary>
    [Test]
    public void FillingThePoolMakesAnOrdinaryLobby()
    {
        Ticket one = Queued(AgeBand.Adult, 2, Pace.Live);
        Ticket two = Queued(AgeBand.Adult, 2, Pace.Live);

        Assert.That(Matchmaker.Fill(_store, _now), Is.EqualTo(1));

        Ticket first = _store.Held(one.Id)!;
        Ticket second = _store.Held(two.Id)!;

        Assert.That(first.Seated, Is.True);
        Assert.That(second.Seated, Is.True);
        Assert.That(first.Code, Is.EqualTo(second.Code), "Both should be in the same match.");
        Assert.That(first.Seat, Is.Not.EqualTo(second.Seat));
        Assert.That(first.SeatToken, Is.Not.EqualTo(second.SeatToken));

        Match match = _store.Find(first.Code!)!;

        Assert.That(match.PlayerCount, Is.EqualTo(2));
        Assert.That(match.Pace, Is.EqualTo(Pace.Live));
        Assert.That(_store.SeatsTaken(match.Code), Is.EqualTo(2));
        Assert.That(match.Started, Is.True);
    }

    [Test]
    public void TwoPacesAreTwoPools()
    {
        Queued(AgeBand.Adult, 2, Pace.Live);
        Queued(AgeBand.Adult, 2, Pace.Anytime);

        Assert.That(
            Matchmaker.Fill(_store, _now), Is.Zero,
            "Somebody who asked for Live must not be dropped into a match that takes a fortnight.");
    }

    [Test]
    public void FillingTwiceDoesNotMoveAnybody()
    {
        Ticket one = Queued(AgeBand.Adult, 2, Pace.Live);
        Queued(AgeBand.Adult, 2, Pace.Live);

        Matchmaker.Fill(_store, _now);

        string? landed = _store.Held(one.Id)!.Code;

        Assert.That(Matchmaker.Fill(_store, _now), Is.Zero);
        Assert.That(_store.Held(one.Id)!.Code, Is.EqualTo(landed));
    }

    /// <summary>
    /// A phone that sent the request and lost signal before the reply asks again. Refusing the
    /// second one would leave a player holding no ticket while the pool holds their place, which is
    /// the one state neither end can get out of.
    /// </summary>
    [Test]
    public void PressingTheButtonTwiceIsTheSameTicket()
    {
        (Account account, string _) = _store.OpenAccount(AgeBand.Adult, _now);

        Ticket first = _store.JoinQueue(account.Id, 2, Pace.Live, _now);
        Ticket again = _store.JoinQueue(account.Id, 2, Pace.Live, _now + TimeSpan.FromSeconds(3));

        Assert.That(again.Id, Is.EqualTo(first.Id));
        Assert.That(_store.Queue(), Has.Count.EqualTo(1));
    }

    [Test]
    public void LeavingTakesTheTicketOutOfThePool()
    {
        Ticket ticket = Queued(AgeBand.Adult, 2, Pace.Live);

        Assert.That(_store.LeaveQueue(ticket.Id), Is.True);
        Assert.That(_store.Queue(), Is.Empty);
        Assert.That(_store.LeaveQueue(ticket.Id), Is.False);
    }

    // ---- Through the pipeline ------------------------------------------------------------

    [Test]
    public async Task AnAdultIsLetIntoThePoolAndAChildIsNot()
    {
        using RelayFactory relay = new RelayFactory($"pool-api-{Guid.NewGuid():N}");
        using HttpClient client = relay.CreateClient();

        Opened adult = await Account(client, AgeBand.Adult);
        Opened child = await Account(client, AgeBand.Child);

        Assert.That(adult.Band, Is.EqualTo("Adult"));

        HttpResponseMessage allowed = await Queue(client, adult, 2, Pace.Live);
        HttpResponseMessage refused = await Queue(client, child, 2, Pace.Live);

        Assert.That(allowed.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.Accepted));
        Assert.That(refused.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task AnAccountThatWasNeverAskedIsNotLetInEither()
    {
        using RelayFactory relay = new RelayFactory($"pool-unasked-{Guid.NewGuid():N}");
        using HttpClient client = relay.CreateClient();

        Opened unasked = await Account(client, AgeBand.Unknown);

        HttpResponseMessage refused = await Queue(client, unasked, 2, Pace.Live);

        Assert.That(refused.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task WithoutAnAccountThereIsNoPoolAtAll()
    {
        using RelayFactory relay = new RelayFactory($"pool-none-{Guid.NewGuid():N}");
        using HttpClient client = relay.CreateClient();

        HttpRequestMessage asked = new HttpRequestMessage(HttpMethod.Post, "/queue")
        {
            Content = JsonContent.Create(new JoinPool(2, Pace.Live)),
        };

        HttpResponseMessage refused = await client.SendAsync(asked);

        Assert.That(refused.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.Unauthorized));
    }

    /// <summary>
    /// Two strangers press the button and end up in the same match, through the real relay with its
    /// real background sweep. This is the whole feature in one test.
    /// </summary>
    [Test]
    public async Task TwoStrangersPressTheButtonAndEndUpInOneMatch()
    {
        using RelayFactory relay = new RelayFactory($"pool-two-{Guid.NewGuid():N}");
        using HttpClient client = relay.CreateClient();

        Opened one = await Account(client, AgeBand.Adult);
        Opened two = await Account(client, AgeBand.Adult);

        string first = await Ticketed(client, one);
        string second = await Ticketed(client, two);

        JsonElement seatedOne = await Seated(client, first);
        JsonElement seatedTwo = await Seated(client, second);

        string code = seatedOne.GetProperty("seated").GetProperty("code").GetString()!;

        Assert.That(
            seatedTwo.GetProperty("seated").GetProperty("code").GetString(), Is.EqualTo(code));
        Assert.That(
            seatedOne.GetProperty("seated").GetProperty("seat").GetInt32(),
            Is.Not.EqualTo(seatedTwo.GetProperty("seated").GetProperty("seat").GetInt32()));

        // And the seat token works on the match endpoints, because it is an ordinary seat.
        HttpResponseMessage played = await client.PostPlan(
            code, 1, seatedOne.GetProperty("seated").GetProperty("token").GetString()!,
            new byte[] { 1, 2, 3 });

        Assert.That(played.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.Accepted));
    }

    // ---- Helpers ------------------------------------------------------------------------

    private Ticket Waiting(
        string account, int playerCount, int minutes = 0, Pace pace = Pace.Live) =>
        new Ticket(
            $"ticket-{account}", account, playerCount, pace,
            _now - TimeSpan.FromMinutes(minutes), null, -1, null);

    private Ticket Queued(AgeBand band, int playerCount, Pace pace)
    {
        (Account account, string _) = _store.OpenAccount(band, _now);

        return _store.JoinQueue(account.Id, playerCount, pace, _now);
    }

    private static async Task<Opened> Account(HttpClient client, AgeBand band)
    {
        HttpResponseMessage made = await client.PostAsJsonAsync(
            "/accounts", new OpenAccount(band));

        return (await made.Content.ReadFromJsonAsync<Opened>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
    }

    private static Task<HttpResponseMessage> Queue(
        HttpClient client, Opened account, int playerCount, Pace pace)
    {
        HttpRequestMessage asked = new HttpRequestMessage(HttpMethod.Post, "/queue")
        {
            Content = JsonContent.Create(new JoinPool(playerCount, pace)),
        };

        asked.Headers.Add("X-Account", account.Id);
        asked.Headers.Add("X-Account-Secret", account.Secret);

        return client.SendAsync(asked);
    }

    private static async Task<string> Ticketed(HttpClient client, Opened account)
    {
        HttpResponseMessage queued = await Queue(client, account, 2, Pace.Live);

        return (await queued.Json()).GetProperty("ticket").GetString()!;
    }

    /// <summary>Waits for the relay's own sweep to seat a ticket, rather than seating it here.</summary>
    private static async Task<JsonElement> Seated(HttpClient client, string ticket)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            JsonElement asked = await (await client.GetAsync($"/queue/{ticket}")).Json();

            if (!asked.GetProperty("waiting").GetBoolean())
            {
                return asked;
            }

            await Task.Delay(50);
        }

        Assert.Fail("The pool never seated anybody.");

        return default;
    }
}
