using System.Net;
using System.Text.Json;
using Relay.Api;

namespace Relay.Tests;

/// <summary>
/// The relay over HTTP, from opening a lobby to a round resolving.
/// </summary>
[TestFixture]
public sealed class RelayEndpointTests
{
    private RelayFactory _relay = null!;
    private HttpClient _client = null!;

    [SetUp]
    public void SetUp()
    {
        _relay = new RelayFactory($"api-{TestContext.CurrentContext.Test.ID}");
        _client = _relay.CreateClient();
    }

    [TearDown]
    public void TearDown()
    {
        _client.Dispose();
        _relay.Dispose();
    }

    // ---- Lobbies ------------------------------------------------------------------------

    [Test]
    public async Task HealthAnswers()
    {
        HttpResponseMessage response = await _client.GetAsync("/health");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task OpeningALobbyHandsBackEverythingNeededToStartSimulating()
    {
        Joined joined = await OpenLobbyFor(2);

        Assert.That(GameCode.IsAllowed(joined.Code), Is.True);
        Assert.That(joined.Seat, Is.EqualTo(0));
        Assert.That(joined.Token, Is.Not.Empty);
        Assert.That(joined.PlayerCount, Is.EqualTo(2));
        Assert.That(joined.Pace, Is.EqualTo("Live"));
        Assert.That(ulong.Parse(joined.Seed, System.Globalization.CultureInfo.InvariantCulture), Is.GreaterThan(0UL));
        Assert.That(joined.Started, Is.False, "One seat of two is not a match yet.");
    }

    /// <summary>
    /// Pace has to survive the wire, and Live being zero is why this needs its own test: an enum that
    /// failed to bind at all would come back as Live and every assertion about the default pace would
    /// agree with the bug. Anytime is the only value that can tell the difference.
    /// </summary>
    [Test]
    public async Task APaceOtherThanTheDefaultSurvivesTheRoundTrip()
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/lobbies", new { playerCount = 2, pace = "Anytime" });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));

        Joined joined = await response.AsJoined();

        Assert.That(joined.Pace, Is.EqualTo("Anytime"));
        Assert.That(
            (await (await _client.GetAsync($"/lobbies/{joined.Code}")).Json())
                .GetProperty("pace").GetString(),
            Is.EqualTo("Anytime"));
    }

    [TestCase(1)]
    [TestCase(5)]
    public async Task ALobbyOutsideTwoToFourPlayersIsRefused(int playerCount)
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/lobbies", new { playerCount, pace = "Live" });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task JoinersGetTheSameSeedAndTheirOwnSeat()
    {
        Joined host = await OpenLobbyFor(2);
        Joined guest = await JoinLobby(host.Code);

        Assert.That(guest.Seed, Is.EqualTo(host.Seed), "One seed, one battlefield.");
        Assert.That(guest.Seat, Is.EqualTo(1));
        Assert.That(guest.Token, Is.Not.EqualTo(host.Token));
        Assert.That(guest.Started, Is.True);
    }

    /// <summary>
    /// The code arrives via a human ear and a human thumb, so the endpoint has to accept what they
    /// produce and not just what the relay printed.
    /// </summary>
    [Test]
    public async Task ACodeTypedInLowerCaseWithPunctuationInItStillJoins()
    {
        Joined host = await OpenLobbyFor(2);
        string typed = host.Code.ToLowerInvariant().Insert(2, "-");

        HttpResponseMessage response = await _client.PostAsync($"/lobbies/{typed}/seats", null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task JoiningAFullLobbyConflicts()
    {
        Joined host = await OpenLobbyFor(2);
        await JoinLobby(host.Code);

        HttpResponseMessage late = await _client.PostAsync($"/lobbies/{host.Code}/seats", null);

        Assert.That(late.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
    }

    [Test]
    public async Task JoiningANonsenseCodeIsNotFound()
    {
        HttpResponseMessage response = await _client.PostAsync("/lobbies/ZZZZZ/seats", null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task ALobbyReportsWhoIsInItWithoutRevealingTokens()
    {
        Joined host = await OpenLobbyFor(3);
        await JoinLobby(host.Code);

        string body = await (await _client.GetAsync($"/lobbies/{host.Code}")).Content.ReadAsStringAsync();
        JsonElement lobby = JsonDocument.Parse(body).RootElement;

        Assert.That(lobby.GetProperty("seated").GetInt32(), Is.EqualTo(2));
        Assert.That(lobby.GetProperty("started").GetBoolean(), Is.False);
        Assert.That(lobby.GetProperty("round").GetInt32(), Is.EqualTo(1));
        Assert.That(body, Does.Not.Contain(host.Token), "A lobby is public. Tokens are not.");
    }

    // ---- Coming back --------------------------------------------------------------------

    /// <summary>
    /// A phone that went to sleep mid-match. The player kept their token, and this is the only way
    /// back in: joining again would hand them a second seat or refuse them as full, and neither is
    /// what somebody reopening the game wants.
    /// </summary>
    [Test]
    public async Task AReturningPlayerGetsTheirOwnSeatBackWithTheSeedAndTheRound()
    {
        Joined host = await OpenLobbyFor(2);
        Joined guest = await JoinLobby(host.Code);

        await _client.PostPlan(host.Code, 1, host.Token, new byte[] { 1 });
        await _client.PostPlan(host.Code, 1, guest.Token, new byte[] { 2 });
        await _client.GetRound(host.Code, 1, host.Token);

        Joined resumed = await (await Resume(host.Code, guest.Token)).AsJoined();

        Assert.That(resumed.Seat, Is.EqualTo(guest.Seat), "Came back as somebody else.");
        Assert.That(resumed.Token, Is.EqualTo(guest.Token));
        Assert.That(resumed.Seed, Is.EqualTo(host.Seed), "Would have grown a different world.");
        Assert.That(resumed.PlayerCount, Is.EqualTo(2));
        Assert.That(resumed.Round, Is.EqualTo(2), "Would have replayed a round already played.");
        Assert.That(resumed.Started, Is.True);
    }

    [Test]
    public async Task ResumingDoesNotTakeAnExtraSeat()
    {
        Joined host = await OpenLobbyFor(3);
        await JoinLobby(host.Code);

        await Resume(host.Code, host.Token);
        await Resume(host.Code, host.Token);

        JsonElement lobby = await (await _client.GetAsync($"/lobbies/{host.Code}")).Json();

        Assert.That(lobby.GetProperty("seated").GetInt32(), Is.EqualTo(2));
    }

    [Test]
    public async Task ResumingWithSomebodyElsesTokenIsRefused()
    {
        Joined mine = await OpenLobbyFor(2);
        Joined elsewhere = await OpenLobbyFor(2);

        Assert.That(
            (await Resume(mine.Code, elsewhere.Token)).StatusCode,
            Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    // ---- Plans --------------------------------------------------------------------------

    [Test]
    public async Task APlanIsAcceptedFromTheSeatThatOwnsTheToken()
    {
        Joined host = await OpenLobbyFor(2);
        await JoinLobby(host.Code);

        HttpResponseMessage response =
            await _client.PostPlan(host.Code, 1, host.Token, new byte[] { 1, 2, 3 });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        Assert.That((await response.Json()).GetProperty("seat").GetInt32(), Is.EqualTo(0));
    }

    [Test]
    public async Task APlanWithoutAKnownTokenIsRefused()
    {
        Joined host = await OpenLobbyFor(2);
        await JoinLobby(host.Code);

        HttpResponseMessage response =
            await _client.PostPlan(host.Code, 1, "not-a-token", new byte[] { 1 });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    /// <summary>
    /// The token is the whole of authorisation in v1, and this is what it is for: a player cannot
    /// commit somebody else's turn even knowing their game code.
    /// </summary>
    [Test]
    public async Task ATokenFromAnotherMatchCannotCommitAPlanHere()
    {
        Joined mine = await OpenLobbyFor(2);
        Joined elsewhere = await OpenLobbyFor(2);

        HttpResponseMessage response =
            await _client.PostPlan(mine.Code, 1, elsewhere.Token, new byte[] { 1 });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task ASecondPlanForTheSameRoundConflicts()
    {
        Joined host = await OpenLobbyFor(2);
        await JoinLobby(host.Code);

        await _client.PostPlan(host.Code, 1, host.Token, new byte[] { 1 });
        HttpResponseMessage again = await _client.PostPlan(host.Code, 1, host.Token, new byte[] { 2 });

        Assert.That(again.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
    }

    [Test]
    public async Task APlanForTheWrongRoundConflictsAndSaysWhichRoundItIsOn()
    {
        Joined host = await OpenLobbyFor(2);
        await JoinLobby(host.Code);

        HttpResponseMessage response =
            await _client.PostPlan(host.Code, 7, host.Token, new byte[] { 1 });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That((await response.Json()).GetProperty("round").GetInt32(), Is.EqualTo(1));
    }

    [Test]
    public async Task AnEmptyPlanIsRefused()
    {
        Joined host = await OpenLobbyFor(2);
        await JoinLobby(host.Code);

        HttpResponseMessage response =
            await _client.PostPlan(host.Code, 1, host.Token, Array.Empty<byte>());

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    /// <summary>
    /// The cap exists to stop the relay being used as a file host, not to constrain the game: one
    /// seat's plan for one round is orders of magnitude under it.
    /// </summary>
    [Test]
    public async Task APlanTooLargeToBeOneIsRefused()
    {
        Joined host = await OpenLobbyFor(2);
        await JoinLobby(host.Code);

        HttpResponseMessage response = await _client.PostPlan(
            host.Code, 1, host.Token, new byte[Limits.LargestPlan + 1]);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    // ---- Resolving ----------------------------------------------------------------------

    /// <summary>
    /// The rule the whole design leans on. Releasing plans early would let the last player to commit
    /// see what everybody else did first, which is the one thing simultaneous turns exist to prevent,
    /// so a partial round hands back a count and nothing else.
    /// </summary>
    [Test]
    public async Task NoPlansAreReleasedUntilEverySeatHasCommitted()
    {
        Joined host = await OpenLobbyFor(3);
        Joined second = await JoinLobby(host.Code);
        await JoinLobby(host.Code);

        await _client.PostPlan(host.Code, 1, host.Token, new byte[] { 1 });
        await _client.PostPlan(host.Code, 1, second.Token, new byte[] { 2 });

        JsonElement round = await (await _client.GetRound(host.Code, 1, host.Token)).Json();

        Assert.That(round.GetProperty("complete").GetBoolean(), Is.False);
        Assert.That(round.GetProperty("waitingOn").GetInt32(), Is.EqualTo(1));
        Assert.That(
            round.TryGetProperty("plans", out JsonElement _),
            Is.False,
            "Two of three plans is still nobody's business.");
    }

    [Test]
    public async Task AFullRoundReleasesEveryPlanExactlyAsItArrived()
    {
        Joined host = await OpenLobbyFor(2);
        Joined guest = await JoinLobby(host.Code);
        byte[] hostPlan = { 0x00, 0xFF, 0x40, 0x00 };
        byte[] guestPlan = { 0x7F, 0x01 };

        await _client.PostPlan(host.Code, 1, host.Token, hostPlan);
        await _client.PostPlan(host.Code, 1, guest.Token, guestPlan);

        JsonElement round = await (await _client.GetRound(host.Code, 1, host.Token)).Json();

        Assert.That(round.GetProperty("complete").GetBoolean(), Is.True);
        Assert.That(round.GetProperty("seed").GetString(), Is.EqualTo(host.Seed));

        JsonElement[] plans = round.GetProperty("plans").EnumerateArray().ToArray();

        Assert.That(plans, Has.Length.EqualTo(2));
        Assert.That(plans[0].GetProperty("seat").GetInt32(), Is.EqualTo(0));
        Assert.That(Convert.FromBase64String(plans[0].GetProperty("payload").GetString()!), Is.EqualTo(hostPlan));
        Assert.That(Convert.FromBase64String(plans[1].GetProperty("payload").GetString()!), Is.EqualTo(guestPlan));
    }

    /// <summary>
    /// Reading a resolved round is what moves the match on, and every client reads it, so the read
    /// has to be idempotent: two clients polling together must not skip a round between them.
    /// </summary>
    [Test]
    public async Task ReadingAResolvedRoundTwiceOnlyAdvancesOnce()
    {
        Joined host = await OpenLobbyFor(2);
        Joined guest = await JoinLobby(host.Code);

        await _client.PostPlan(host.Code, 1, host.Token, new byte[] { 1 });
        await _client.PostPlan(host.Code, 1, guest.Token, new byte[] { 2 });

        await _client.GetRound(host.Code, 1, host.Token);
        await _client.GetRound(host.Code, 1, host.Token);

        JsonElement lobby = await (await _client.GetAsync($"/lobbies/{host.Code}")).Json();

        Assert.That(lobby.GetProperty("round").GetInt32(), Is.EqualTo(2));
    }

    [Test]
    public async Task OnceARoundResolvesTheNextOneAcceptsPlans()
    {
        Joined host = await OpenLobbyFor(2);
        Joined guest = await JoinLobby(host.Code);

        await _client.PostPlan(host.Code, 1, host.Token, new byte[] { 1 });
        await _client.PostPlan(host.Code, 1, guest.Token, new byte[] { 2 });
        await _client.GetRound(host.Code, 1, host.Token);

        HttpResponseMessage next = await _client.PostPlan(host.Code, 2, host.Token, new byte[] { 3 });

        Assert.That(next.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
    }

    // ---- Determinism reports ------------------------------------------------------------

    [Test]
    public async Task AgreeingHashesReadBackAsAgreement()
    {
        Joined host = await OpenLobbyFor(2);
        Joined guest = await JoinLobby(host.Code);

        await ReportHash(host.Code, 1, host.Token, "12345678901234567890");
        await ReportHash(host.Code, 1, guest.Token, "12345678901234567890");

        JsonElement report = await (await _client.GetAsync($"/matches/{host.Code}/hashes")).Json();

        Assert.That(report.GetProperty("diverged").GetBoolean(), Is.False);

        JsonElement round = report.GetProperty("rounds").EnumerateArray().Single();

        Assert.That(round.GetProperty("round").GetInt32(), Is.EqualTo(1));
        Assert.That(round.GetProperty("reported").GetInt32(), Is.EqualTo(2));
        Assert.That(round.GetProperty("agreed").GetBoolean(), Is.True);
    }

    /// <summary>
    /// The whole reason the hashes are collected. Two clients simulated the same round from the same
    /// inputs and got different answers, which is a determinism bug on somebody's real hardware, and
    /// the match is its own reproduction because the seed and every plan are already stored.
    /// </summary>
    [Test]
    public async Task DisagreeingHashesAreReportedAsDivergence()
    {
        Joined host = await OpenLobbyFor(2);
        Joined guest = await JoinLobby(host.Code);

        await ReportHash(host.Code, 1, host.Token, "111");
        await ReportHash(host.Code, 1, guest.Token, "222");

        JsonElement report = await (await _client.GetAsync($"/matches/{host.Code}/hashes")).Json();

        Assert.That(report.GetProperty("diverged").GetBoolean(), Is.True);
        Assert.That(
            report.GetProperty("rounds").EnumerateArray().Single().GetProperty("agreed").GetBoolean(),
            Is.False);
    }

    /// <summary>
    /// A round only half reported is not a divergence. Saying otherwise would flag every match that
    /// is still being played, which would make the signal worthless.
    /// </summary>
    [Test]
    public async Task OneReportOnItsOwnIsNotADivergence()
    {
        Joined host = await OpenLobbyFor(2);
        await JoinLobby(host.Code);

        await ReportHash(host.Code, 1, host.Token, "111");

        JsonElement report = await (await _client.GetAsync($"/matches/{host.Code}/hashes")).Json();

        Assert.That(report.GetProperty("diverged").GetBoolean(), Is.False);
        Assert.That(
            report.GetProperty("rounds").EnumerateArray().Single().GetProperty("reported").GetInt32(),
            Is.EqualTo(1));
    }

    /// <summary>
    /// A client that restarts and reports again must not overwrite its first answer, because that is
    /// exactly the disagreement the endpoint exists to catch.
    /// </summary>
    [Test]
    public async Task ASecondReportFromTheSameSeatDoesNotEraseTheFirst()
    {
        Joined host = await OpenLobbyFor(2);
        Joined guest = await JoinLobby(host.Code);

        await ReportHash(host.Code, 1, host.Token, "111");
        await ReportHash(host.Code, 1, host.Token, "999");
        await ReportHash(host.Code, 1, guest.Token, "111");

        JsonElement report = await (await _client.GetAsync($"/matches/{host.Code}/hashes")).Json();

        Assert.That(report.GetProperty("diverged").GetBoolean(), Is.False);
    }

    /// <summary>
    /// A hash is an unsigned 64-bit number, and half of them do not fit in the signed integer a JSON
    /// number is often read as, which is why it travels as a string. This is the value that proves
    /// the string is not a decoration.
    /// </summary>
    [Test]
    public async Task AHashPastLongMaxValueSurvivesTheRoundTrip()
    {
        Joined host = await OpenLobbyFor(2);
        const string past = "18446744073709551615";

        await ReportHash(host.Code, 1, host.Token, past);

        JsonElement report = await (await _client.GetAsync($"/matches/{host.Code}/hashes")).Json();
        JsonElement hashes = report.GetProperty("rounds").EnumerateArray().Single().GetProperty("hashes");

        Assert.That(hashes.EnumerateArray().Single().GetUInt64().ToString(
            System.Globalization.CultureInfo.InvariantCulture), Is.EqualTo(past));
    }

    [Test]
    public async Task AHashThatIsNotANumberIsRefused()
    {
        Joined host = await OpenLobbyFor(2);

        HttpResponseMessage response = await ReportHash(host.Code, 1, host.Token, "-1");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task AHashWithoutAKnownTokenIsRefused()
    {
        Joined host = await OpenLobbyFor(2);

        HttpResponseMessage response = await ReportHash(host.Code, 1, "not-a-token", "111");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    // ---- Helpers ------------------------------------------------------------------------

    private Task<HttpResponseMessage> Resume(string code, string token)
    {
        HttpRequestMessage request =
            new HttpRequestMessage(HttpMethod.Get, $"/matches/{code}/seat");
        request.Headers.Add("X-Seat-Token", token);

        return _client.SendAsync(request);
    }

    private Task<HttpResponseMessage> ReportHash(string code, int round, string token, string hash)
    {
        HttpRequestMessage request = new HttpRequestMessage(
            HttpMethod.Post, $"/matches/{code}/rounds/{round}/hash")
        {
            Content = JsonContent.Create(new { hash }),
        };
        request.Headers.Add("X-Seat-Token", token);

        return _client.SendAsync(request);
    }


    private async Task<Joined> OpenLobbyFor(int playerCount)
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/lobbies", new { playerCount, pace = "Live" });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));

        return await response.AsJoined();
    }

    private async Task<Joined> JoinLobby(string code)
    {
        HttpResponseMessage response = await _client.PostAsync($"/lobbies/{code}/seats", null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        return await response.AsJoined();
    }
}
